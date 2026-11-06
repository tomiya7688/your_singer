using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

public static class SharedFeatureSupport
{
    public const string Supported = "supported";
    public const string Unsupported = "unsupported";
}

// 1つの特徴についての受入判定。根拠は固定版ソースのコード調査(docs/design/v1-training/routing-and-cross-domain.md)。
public sealed record BackendFeatureCapability(string Feature, string Support, string Evidence, string ReasonJa);

public sealed class BackendCapability
{
    public required string Backend { get; init; }
    public required string Revision { get; init; }
    // このBackendが学習対象とする用途(talk または singing)。
    public required string Target { get; init; }
    public required IReadOnlyList<BackendFeatureCapability> Features { get; init; }
}

// 共有特徴の受入を要求する側が渡す契約。特徴の参照だけを持ち、音声や教師ラベルは含めない
// (feature_only の区間を別用途の教師音声として投入できない型にする)。
public sealed class SharedFeatureAdapterRequest
{
    public required string Backend { get; init; }
    public required string Revision { get; init; }
    public required string Target { get; init; }
    public required string SpeakerId { get; init; }
    public required SharedSpeakerFeatureSet Features { get; init; }
}

public sealed class SharedFeatureUsageReport
{
    public required string Backend { get; init; }
    public required string Revision { get; init; }
    public required string Target { get; init; }
    public required IReadOnlyList<SharedFeatureUsageEntry> Entries { get; init; }
    // 1つでも学習に使えたか。v1の固定版では直接conditioningできる特徴が無く false。
    public bool AnyUsed => Entries.Any(x => x.Used);
}

// 固定版Backend(DiffSinger v2.5.1 / Style-Bert-VITS2 2.7.0)の共有特徴の受入能力表。
// 判断はコード調査に基づく。調査していない能力を supported にしない。
public static class BackendCapabilities
{
    private static readonly string[] Names =
        [SharedFeatureName.SpeakerEmbedding, SharedFeatureName.F0Distribution, SharedFeatureName.StyleProsody];

    public static readonly BackendCapability DiffSinger = new()
    {
        Backend = TrainingBackendCatalog.SingingBackend, Revision = TrainingBackendCatalog.SingingRevision, Target = "singing",
        Features =
        [
            new(SharedFeatureName.SpeakerEmbedding, SharedFeatureSupport.Unsupported,
                "modules/fastspeech/acoustic_encoder.py:60-62,102-108",
                "話者は学習済みの話者IDの表(Embedding)で表され、外部で計算した連続の話者ベクトルは入力できません。"),
            new(SharedFeatureName.F0Distribution, SharedFeatureSupport.Unsupported,
                "modules/fastspeech/acoustic_encoder.py:83,110-112",
                "F0は区間ごとのフレーム列として入力され、話者のF0分布(平均・ばらつき)という要約は入力できません。"),
            new(SharedFeatureName.StyleProsody, SharedFeatureSupport.Unsupported,
                "modules/fastspeech/acoustic_encoder.py:32-58",
                "エネルギー等はフレーム列で音声から導出され、話し方の要約(速さ・間など)は入力できません。")
        ]
    };

    public static readonly BackendCapability StyleBertVits2 = new()
    {
        Backend = TrainingBackendCatalog.TalkBackend, Revision = TrainingBackendCatalog.TalkRevision, Target = "talk",
        Features =
        [
            new(SharedFeatureName.SpeakerEmbedding, SharedFeatureSupport.Unsupported,
                "style_bert_vits2/models/models_jp_extra.py:1006-1007,1033-1034",
                "話者は話者IDの表(Embedding)で表されます。スタイルベクトル(256次元)は音声から作る別の特徴で、共有の話者ベクトルとは意味が異なるため直接は使えません。"),
            new(SharedFeatureName.F0Distribution, SharedFeatureSupport.Unsupported,
                "style_bert_vits2/models/models_jp_extra.py(F0の入力引数なし)",
                "F0を明示的に入力する経路がありません。"),
            new(SharedFeatureName.StyleProsody, SharedFeatureSupport.Unsupported,
                "style_bert_vits2/models/models.py:1063-1067",
                "推論時の速さ等の係数はありますが、話者の話し方の要約を条件として学習に渡す経路はありません。")
        ]
    };

    public static BackendCapability For(string backend, string revision)
    {
        var found = new[] { DiffSinger, StyleBertVits2 }.FirstOrDefault(x => x.Backend == backend)
            ?? throw new ArgumentException($"能力表にないBackendです: {backend}");
        if (found.Revision != revision)
            throw new ArgumentException($"{backend} のrevisionが固定版と一致しません(固定: {found.Revision}、指定: {revision})。");
        return found;
    }

    // {
    //   責務: [Evaluate: 共有特徴の受入可否を判定し、ジョブに残す利用記録を作る]
    //   処理: [
    //     1: Backend・revision・用途・話者の一致を検証する(別話者の特徴・別用途のBackendを拒否)
    //     2: 測定済みの特徴ごとに能力表を引き、未対応は理由付きで記録する
    //     3: 実利用が無いときは Used=false のまま返す(相互学習済みと表示しない根拠)
    //   ]
    //   エラー: [revision・用途・話者が一致しない場合に ArgumentException]
    // }
    public static SharedFeatureUsageReport Evaluate(SharedFeatureAdapterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capability = For(request.Backend, request.Revision);
        if (capability.Target != request.Target)
            throw new ArgumentException($"{request.Backend} は {capability.Target} 用で、{request.Target} には使えません。");
        if (request.Features.SpeakerId != request.SpeakerId)
            throw new ArgumentException("共有特徴の話者が要求の話者と一致しません(別話者の特徴は混ぜません)。");

        var entries = request.Features.Features
            .Where(x => x.Status == ConfidenceStatus.Measured)
            .Select(feature =>
            {
                var rule = capability.Features.Single(x => x.Feature == feature.Feature);
                return new SharedFeatureUsageEntry
                {
                    Feature = feature.Feature, Domain = feature.Domain, Support = rule.Support,
                    Reason = rule.ReasonJa, Used = rule.Support == SharedFeatureSupport.Supported
                };
            }).ToArray();
        return new SharedFeatureUsageReport
        {
            Backend = capability.Backend, Revision = capability.Revision, Target = capability.Target, Entries = entries
        };
    }

    public static IReadOnlyList<string> FeatureNames => Names;
}
