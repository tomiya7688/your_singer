using System.Text.RegularExpressions;
using YourSinger.Data.Models;

namespace YourSinger.Data.Processing;

// 学習ジョブの学習方式(質感・モード・初期化)の検証。作成時は ArgumentException、保存データは InvalidDataException。
public static partial class TrainingJobSettingsValidator
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    // {
    //   責務: [ValidateOptions: ジョブ作成時の指定が契約に合うか検証する]
    //   処理: [
    //     1: モード・質感を検証する
    //     2: from_scratch でベース重みの指定を拒否し、fine_tune で重みを必須にする
    //   ]
    //   引数: [options: 用途別の指定。nullは既定として許可する]
    //   エラー: [契約に反する場合に ArgumentException]
    // }
    public static void ValidateOptions(TrainingJobOptions? options)
    {
        if (options is null) return;
        try
        {
            RequireDefined(options.Mode);
            if (options.Texture is not null) ValidateTexture(options.Texture);
            if (options.BaseCheckpoints is null) throw Fail("ベース重みの一覧がありません。");
            if (options.Mode == TrainingMode.FromScratch && options.BaseCheckpoints.Count > 0)
                throw Fail("最初から新規学習(from_scratch)ではベース話者モデルの重みを指定できません。");
            if (options.Mode == TrainingMode.FineTune && options.BaseCheckpoints.Count == 0)
                throw Fail("追加学習(fine_tune)には初期重みの指定が必要です。");
            if (options.Mode == TrainingMode.Auto && options.BaseCheckpoints.Count > 0)
                throw Fail("自動(auto)ではベース重みを直接指定できません。追加学習を選んでください。");
            foreach (var component in options.BaseCheckpoints) ValidateComponent(component);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, nameof(options));
        }
    }

    // {
    //   責務: [ValidateJob: 保存済みジョブの学習方式が契約に合うか検証する]
    //   処理: [
    //     1: 旧ジョブ(モード無し)は他の新項目が無いことだけ確認する
    //     2: モード・質感・解決済みモードと初期化の整合を検証する
    //   ]
    //   エラー: [契約に反する場合に InvalidDataException]
    // }
    public static void ValidateJob(TrainingJobRecord job)
    {
        if (job.RequestedTrainingMode is null)
        {
            if (job.ResolvedTrainingMode is not null || job.Initialization is not null || job.RequestedTexture is not null)
                throw Fail($"{job.JobId}: requested_training_mode が無いのに学習方式の項目があります。");
            return;
        }
        RequireDefined(job.RequestedTrainingMode.Value);
        if (job.RequestedTexture is null) throw Fail($"{job.JobId}: requested_texture がありません。");
        ValidateTexture(job.RequestedTexture);
        if (job.ResolvedTrainingMode is null)
        {
            if (job.RequestedTrainingMode != TrainingMode.Auto)
                throw Fail($"{job.JobId}: 明示したモードは resolved_training_mode が必要です。");
            if (job.Initialization is not null)
                throw Fail($"{job.JobId}: モード未解決のジョブに初期化情報があります。");
            return;
        }
        RequireDefined(job.ResolvedTrainingMode.Value);
        if (job.ResolvedTrainingMode == TrainingMode.Auto)
            throw Fail($"{job.JobId}: resolved_training_mode に auto は指定できません。");
        if (job.RequestedTrainingMode != TrainingMode.Auto && job.RequestedTrainingMode != job.ResolvedTrainingMode)
            throw Fail($"{job.JobId}: 指定したモードと解決済みモードが異なります。");
        var initialization = job.Initialization ?? throw Fail($"{job.JobId}: 初期化情報がありません。");
        if (initialization.Components is null) throw Fail($"{job.JobId}: 初期化の構成要素がありません。");
        if (job.ResolvedTrainingMode == TrainingMode.FromScratch)
        {
            if (initialization.Type != TrainingJobVocabulary.RandomInitialization || initialization.Components.Count > 0)
                throw Fail($"{job.JobId}: from_scratch は random_initialization で構成要素が空である必要があります。");
            return;
        }
        if (initialization.Type != TrainingJobVocabulary.PretrainedCheckpoint || initialization.Components.Count == 0)
            throw Fail($"{job.JobId}: fine_tune は pretrained_checkpoint と初期重みが必要です。");
        foreach (var component in initialization.Components) ValidateComponent(component);
    }

    private static void ValidateTexture(TrainingTextureRequest texture)
    {
        if (string.IsNullOrWhiteSpace(texture.Preset)) throw Fail("質感のpresetが空です。");
        if (texture.ReferenceSegmentIds is null || texture.ReferenceSegmentIds.Any(string.IsNullOrWhiteSpace) ||
            texture.ReferenceSegmentIds.Distinct(StringComparer.Ordinal).Count() != texture.ReferenceSegmentIds.Count)
            throw Fail("質感の参考区間IDが不正です(空または重複)。");
    }

    private static void ValidateComponent(TrainingInitializationComponent? component)
    {
        if (component is null || string.IsNullOrWhiteSpace(component.Name))
            throw Fail("初期重みの名前が空です。");
        if (!Sha256Pattern().IsMatch(component.Sha256 ?? string.Empty))
            throw Fail($"初期重み {component.Name} のsha256が不正です(64桁の16進数が必要です)。");
        var path = component.Path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
            path.Split('/', '\\').Any(x => x == ".."))
            throw Fail($"初期重み {component.Name} のpathが作業領域内の相対パスではありません。");
    }

    private static void RequireDefined(TrainingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw Fail($"学習モードの値が不正です: {(int)mode}");
    }

    private static InvalidDataException Fail(string cause) => new(cause);
}
