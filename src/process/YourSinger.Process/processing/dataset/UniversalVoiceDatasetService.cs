using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetService: 音声ソースから共通データセットを構築して保存する
//   ]
//   フィールド: [
//     SchemaVersion: 保存するdataset schemaの版
//     StageVersion: 特徴抽出・キャッシュの処理版
//     worker: 音響特徴抽出を実行するMLワーカー
//     repositories: 前処理、話者、内容分類、datasetの保存先
//     cacheKeyService: 音声区間のキャッシュキー作成器
//   ]
//   処理: [
//     1: 既存情報を読み込みsegment特徴を再利用または抽出する
//     2: 修正・来歴・カバレッジを反映して保存する
//   ]
// }
public sealed class UniversalVoiceDatasetService
{
    public const string SchemaVersion = "1";
    public const string StageVersion = "v1-05.5-mfa-measurement";

    private readonly MlWorkerClient _worker;
    private readonly AudioPreprocessingRepository _preprocessingRepository;
    private readonly SpeakerAnalysisRepository _speakerRepository;
    private readonly ContentClassificationRepository _classificationRepository;
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly FeatureCacheKeyService _cacheKeyService;

    // {
    //   責務: [UniversalVoiceDatasetService: dataset構築に必要な依存サービスを受け取る]
    //   引数: [worker: MLワーカー, repositories: dataset関連の読み書き先, cacheKeyService: キャッシュキー作成器]
    // }
    public UniversalVoiceDatasetService(
        MlWorkerClient worker,
        AudioPreprocessingRepository preprocessingRepository,
        SpeakerAnalysisRepository speakerRepository,
        ContentClassificationRepository classificationRepository,
        UniversalVoiceDatasetRepository datasetRepository,
        FeatureCacheKeyService cacheKeyService)
    {
        _worker = worker;
        _preprocessingRepository = preprocessingRepository;
        _speakerRepository = speakerRepository;
        _classificationRepository = classificationRepository;
        _datasetRepository = datasetRepository;
        _cacheKeyService = cacheKeyService;
    }

    // {
    //   責務: [
    //     BuildAsync: 前処理済み音声区間から共通データセットを作成または更新する
    //   ]
    //   処理: [
    //     1: 既存特徴、話者、内容分類、利用者修正を読み込む
    //     2: 各音声区間のキャッシュを確認し必要な特徴を抽出する
    //     3: 修正と来歴を適用しカバレッジを再集計して保存する
    //   ]
    //   引数: [
    //     workspace: 作業領域
    //     sources: 処理対象の音声ソース
    //     cancellationToken: 処理中断要求
    //   ]
    //   戻り値: [
    //     dataset: 保存した共通データセット
    //   ]
    //   エラー: [
    //     読み込み、特徴抽出、保存に失敗した場合に失敗する
    //   ]
    //   副作用: [
    //     ワーカーを呼び出し共通データセットを保存する
    //   ]
    // }
    public async Task<UniversalVoiceDatasetRecord> BuildAsync(
        ProjectWorkspace workspace,
        IEnumerable<SourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadBuildContextAsync(workspace, cancellationToken);
        // 入力ソースごとの抽出結果を、同じ作成コンテキストへ追加する。
        foreach (var source in sources)
        {
            await AppendSourceAsync(context, workspace, source, cancellationToken);
        }

        // 利用者修正と来歴を反映した後、保存前に派生カバレッジを再集計する。
        UniversalVoiceDatasetOverrideApplicator.Apply(context.Dataset);
        UniversalVoiceDatasetProvenanceBuilder.UpdatePhonemeMethods(context.Dataset);
        UniversalVoiceDatasetCoverageBuilder.Rebuild(context.Dataset);

        // 全segmentと来歴が揃ったデータセットを一度だけ保存する。
        await _datasetRepository.SaveAsync(workspace, context.Dataset, cancellationToken);
        return context.Dataset;
    }

    // {
    //   責務: [LoadBuildContextAsync: dataset構築で共有する既存情報と分類情報を読み込む]
    //   処理: [1: 既存dataset、話者情報、内容分類を読み、検索用辞書を作る]
    //   引数: [workspace: 作業領域, cancellationToken: 処理中断要求]
    //   戻り値: [context: dataset構築に必要な状態]
    //   エラー: [リポジトリ読み込みに失敗した場合に失敗する]
    // }
    private async Task<UniversalVoiceDatasetBuildContext> LoadBuildContextAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        // キャッシュ再利用と修正適用のため、前回datasetと話者・分類情報を読む。
        var existing = await _datasetRepository.LoadAsync(workspace, cancellationToken);
        var existingBySegment = existing?.Segments.ToDictionary(segment => segment.SegmentId)
            ?? new Dictionary<string, UniversalVoiceSegment>(StringComparer.Ordinal);

        var speakers = await _speakerRepository.LoadAsync(workspace, cancellationToken);
        var speakerBySegment = speakers?.Assignments.ToDictionary(
            assignment => assignment.SegmentId,
            assignment => assignment.SpeakerId)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var speakerEmbeddingById = speakers?.Speakers.ToDictionary(
            speaker => speaker.SpeakerId,
            speaker => speaker.EmbeddingCentroid)
            ?? new Dictionary<string, List<double>>(StringComparer.Ordinal);

        var classification = await _classificationRepository.LoadAsync(
            workspace,
            cancellationToken);
        var contentBySegment = classification?.Classifications.ToDictionary(
            item => item.SegmentId)
            ?? new Dictionary<string, SegmentContentClassification>(StringComparer.Ordinal);

        var dataset = new UniversalVoiceDatasetRecord
        {
            SchemaVersion = SchemaVersion,
            StageVersion = StageVersion
        };
        if (existing is not null)
        {
            dataset.Overrides.AddRange(existing.Overrides);
        }

        return new UniversalVoiceDatasetBuildContext(
            dataset,
            existing,
            existingBySegment,
            speakerBySegment,
            speakerEmbeddingById,
            contentBySegment);
    }

    // {
    //   責務: [AppendSourceAsync: 前処理済みsegmentをdataset構築へ追加する]
    //   処理: [1: 前処理結果を読み、各segmentの特徴を構築コンテキストへ追加する]
    //   引数: [context: dataset構築状態, workspace: 作業領域, source: 入力ソース, cancellationToken: 処理中断要求]
    //   戻り値: []
    //   エラー: [前処理結果の読み込みまたは特徴抽出に失敗した場合に失敗する]
    // }
    private async Task AppendSourceAsync(
        UniversalVoiceDatasetBuildContext context,
        ProjectWorkspace workspace,
        SourceRecord source,
        CancellationToken cancellationToken)
    {
        // 前処理結果のないソースは、共通データセットの対象に含めない。
        var preprocessing = await _preprocessingRepository.LoadAsync(
            workspace,
            source.SourceId,
            cancellationToken);
        if (preprocessing is null || preprocessing.ErrorMessage is not null)
        {
            return;
        }

        foreach (var segment in preprocessing.Segments)
        {
            await AppendSegmentAsync(context, workspace, source, segment, cancellationToken);
        }
    }

    // {
    //   責務: [AppendSegmentAsync: segmentの特徴をキャッシュ再利用またはワーカー抽出で用意する]
    //   処理: [1: キャッシュキーを作る 2: 有効な既存特徴を再利用する 3: 不足時はワーカー抽出結果を追加する]
    //   引数: [context: dataset構築状態, workspace: 作業領域, source: 入力ソース, segment: 前処理済みsegment, cancellationToken: 処理中断要求]
    //   戻り値: []
    //   エラー: [ワーカー呼び出しまたは特徴結果の読み取りに失敗した場合に失敗する]
    // }
    private async Task AppendSegmentAsync(
        UniversalVoiceDatasetBuildContext context,
        ProjectWorkspace workspace,
        SourceRecord source,
        AudioSegmentRecord segment,
        CancellationToken cancellationToken)
    {
        // 音声内容と抽出段階からキャッシュキーを作り、再利用可否を判定する。
        var audioPath = Path.Combine(workspace.RootPath, segment.AudioPath);
        context.TranscriptBySegment.TryGetValue(segment.SegmentId, out var transcriptOverride);
        // 修正歌詞を含む抽出結果は音声だけで再利用せず、本文のhashを処理条件へ含める。
        var cacheVersion = transcriptOverride is null ? StageVersion : StageVersion + "/transcript/" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transcriptOverride)));
        var cacheKey = await _cacheKeyService.CreateAsync(
            audioPath,
            cacheVersion,
            cancellationToken);
        if (context.ExistingSegmentsById.TryGetValue(segment.SegmentId, out var cached) &&
            cached.CacheKey == cacheKey &&
            cached.AlignmentMethod != "invalidated-by-transcript-override" &&
            UniversalVoiceDatasetSegmentReader.HasFeatureFiles(workspace, cached) &&
            await MfaMeasurementValidator.CanReuseAsync(workspace, cached, cancellationToken))
        {
            AppendCachedSegment(context, cached);
            return;
        }

        var result = await _worker.SendAsync(
            "extract_universal_features",
            new
            {
                segment_id = segment.SegmentId,
                source_id = source.SourceId,
                audio_path = audioPath,
                workspace_path = workspace.RootPath,
                cache_key = cacheKey,
                transcript_override = transcriptOverride,
                stage_version = StageVersion
            },
            cancellationToken);

        // ワーカー出力を内部モデルへ変換し、既存の話者・分類情報を付与する。
        var feature = UniversalVoiceDatasetSegmentReader.ParseWorkerResult(result);
        // { 処理: [要求した音声と実測を照合してからworker結果を保存する] }
        if (!string.Equals(Path.GetFullPath(feature.AudioPath, workspace.RootPath),
                Path.GetFullPath(segment.AudioPath, workspace.RootPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("特徴抽出結果の音声が要求した区間と一致しません。");
        await MfaMeasurementValidator.ValidateFeatureAsync(workspace, feature, cancellationToken);
        AttachSpeakerAndContent(context, segment.SegmentId, feature);
        context.Dataset.Segments.Add(feature);
        UniversalVoiceDatasetProvenanceBuilder.AddForSegment(
            context.Dataset,
            feature,
            StageVersion);
    }

    // {
    //   責務: [AppendCachedSegment: キャッシュ済みsegmentと対応する来歴を追加する]
    //   処理: [1: segmentを再利用し、同じキャッシュキーの来歴だけをコピーする]
    //   引数: [context: dataset構築状態, segment: 再利用するsegment]
    //   戻り値: []
    //   副作用: [context.Datasetのsegment一覧と来歴を書き換える]
    // }
    private static void AppendCachedSegment(
        UniversalVoiceDatasetBuildContext context,
        UniversalVoiceSegment segment)
    {
        context.Dataset.Segments.Add(segment);
        if (context.ExistingDataset is null)
        {
            return;
        }

        context.Dataset.Provenance.AddRange(
            context.ExistingDataset.Provenance.Where(item =>
                item.SegmentId == segment.SegmentId && item.CacheKey == segment.CacheKey));
    }

    // {
    //   責務: [AttachSpeakerAndContent: 既存の話者割り当てと内容分類をsegmentへ関連付ける]
    //   処理: [1: 内容分類を設定する 2: 話者IDと利用可能な埋め込みを設定する]
    //   引数: [context: dataset構築状態, segmentId: 対象segment ID, feature: 更新するsegment]
    //   戻り値: []
    //   副作用: [featureの話者・内容分類フィールドを書き換える]
    // }
    private static void AttachSpeakerAndContent(
        UniversalVoiceDatasetBuildContext context,
        string segmentId,
        UniversalVoiceSegment feature)
    {
        feature.ContentType = context.ContentBySegment.TryGetValue(segmentId, out var classification)
            ? classification.ContentType
            : SegmentContentType.Ambiguous;

        if (!context.SpeakerBySegment.TryGetValue(segmentId, out var speakerId))
        {
            return;
        }

        feature.SpeakerId = speakerId;
        if (context.SpeakerEmbeddingById.TryGetValue(speakerId, out var embedding))
        {
            feature.SpeakerEmbedding.AddRange(embedding);
        }
    }

}
