using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

public static class SharedFeatureName
{
    public const string SpeakerEmbedding = "speaker_embedding";
    public const string F0Distribution = "f0_distribution";
    public const string StyleProsody = "style_prosody";
}

public static class SharedFeatureDomain
{
    public const string Speech = "speech";
    public const string Singing = "singing";
}

// 1つの特徴(特徴名×用途)の参照。どの区間から作った値かを SegmentIds で追跡できる。
public sealed class SharedFeatureReference
{
    public required string Feature { get; init; }
    public required string Domain { get; init; }
    // measured または unavailable(観測が無い)。観測が無いときに補完値を入れない。
    public required string Status { get; init; }
    // 値の作成に使った区間(SegmentId昇順)。unavailable は空。
    public required IReadOnlyList<string> SegmentIds { get; init; }
    public IReadOnlyList<double>? Vector { get; init; }
    public IReadOnlyDictionary<string, double>? Scalars { get; init; }
    public required string MeasurementVersion { get; init; }
}

// 集約に使わなかった区間と理由。別話者・除外・確認待ちを混ぜていないことの記録。
public sealed record SharedFeatureSkip(string SegmentId, string Feature, string Domain, string Reason);

public sealed class SharedSpeakerFeatureSet
{
    public required string SpeakerId { get; init; }
    public required IReadOnlyList<SharedFeatureReference> Features { get; init; }
    public required IReadOnlyList<SharedFeatureSkip> Skipped { get; init; }

    public SharedFeatureReference Find(string feature, string domain) =>
        Features.Single(x => x.Feature == feature && x.Domain == domain);
}

// 同一話者のトーク・歌唱それぞれの共通特徴を、来歴(source_segment_id)付きで集約する。
// 対応可否の判断や学習器への反映はしない(D-08: #98 / 後続)。原音声やデータセットは変更しない。
// 正本: docs/design/v1-training/routing-and-cross-domain.md「トーク／歌唱間の話者特徴共有」。
public sealed class SharedSpeakerFeatureService
{
    public const string MeasurementVersion = "shared-speaker-feature-v1";
    private static readonly string[] Domains = [SharedFeatureDomain.Speech, SharedFeatureDomain.Singing];

    // {
    //   責務: [Build: 対象話者の用途別 primary 区間から、共通特徴の参照を決定的に作る]
    //   処理: [
    //     1: routing が対象話者かつその用途の primary とした区間だけを採用する(別話者・話者不明・除外・確認待ちは混ぜない)
    //     2: 話者embedding・F0分布・style/prosody を測定済みの区間だけで等重みに集約する
    //     3: 観測が無い特徴は unavailable で空の来歴にする
    //   ]
    //   戻り値: [set: 特徴名×用途の参照(固定順)と、使わなかった区間の理由]
    // }
    public SharedSpeakerFeatureSet Build(
        IEnumerable<UniversalVoiceSegment> segments,
        UsageRoutingResult routing,
        string speakerId)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(routing);
        if (string.IsNullOrWhiteSpace(speakerId))
            throw new ArgumentException("話者IDが空です。", nameof(speakerId));
        var byId = segments.GroupBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var skipped = new List<SharedFeatureSkip>();
        var features = new List<SharedFeatureReference>();
        var decisions = routing.Decisions.Where(x => x.SpeakerId == speakerId).ToArray();

        foreach (var domain in Domains)
        {
            var members = new List<UniversalVoiceSegment>();
            foreach (var decision in decisions.OrderBy(x => x.SegmentId, StringComparer.Ordinal))
            {
                var assignment = domain == SharedFeatureDomain.Speech ? decision.Talk : decision.Singing;
                if (assignment.Role == UsageRole.Primary && byId.TryGetValue(decision.SegmentId, out var segment))
                    members.Add(segment);
                else
                    foreach (var feature in FeatureNames())
                        skipped.Add(new(decision.SegmentId, feature, domain, assignment.ReasonCodes.FirstOrDefault() ?? assignment.Role));
            }
            features.Add(Embedding(members, domain, skipped));
            features.Add(F0(members, domain, skipped));
            features.Add(Style(members, domain, skipped));
        }
        return new SharedSpeakerFeatureSet
        {
            SpeakerId = speakerId,
            Features = features.OrderBy(x => x.Feature, StringComparer.Ordinal)
                .ThenBy(x => x.Domain, StringComparer.Ordinal).ToArray(),
            Skipped = skipped.OrderBy(x => x.SegmentId, StringComparer.Ordinal)
                .ThenBy(x => x.Feature, StringComparer.Ordinal).ThenBy(x => x.Domain, StringComparer.Ordinal).ToArray()
        };
    }

    private static string[] FeatureNames() =>
        [SharedFeatureName.SpeakerEmbedding, SharedFeatureName.F0Distribution, SharedFeatureName.StyleProsody];

    // 同じ次元の有限なembeddingだけを平均する。最初に見つかった次元に合わない区間は理由付きで除く。
    private static SharedFeatureReference Embedding(
        List<UniversalVoiceSegment> members, string domain, List<SharedFeatureSkip> skipped)
    {
        var usable = new List<UniversalVoiceSegment>();
        foreach (var segment in members)
        {
            var embedding = segment.SpeakerEmbedding;
            if (embedding is not { Count: > 0 } || embedding.Any(x => !double.IsFinite(x)))
                skipped.Add(new(segment.SegmentId, SharedFeatureName.SpeakerEmbedding, domain, "embedding_unavailable"));
            else if (usable.Count > 0 && usable[0].SpeakerEmbedding.Count != embedding.Count)
                skipped.Add(new(segment.SegmentId, SharedFeatureName.SpeakerEmbedding, domain, "embedding_dimension_mismatch"));
            else
                usable.Add(segment);
        }
        if (usable.Count == 0) return Unavailable(SharedFeatureName.SpeakerEmbedding, domain);
        var dimension = usable[0].SpeakerEmbedding.Count;
        var mean = Enumerable.Range(0, dimension).Select(i => usable.Average(x => x.SpeakerEmbedding[i])).ToArray();
        return Measured(SharedFeatureName.SpeakerEmbedding, domain, usable, vector: mean);
    }

    // 平均F0は区間平均の等重み平均、標準偏差は全分散の法則(区間内分散+区間平均の分散)で合成する。
    private static SharedFeatureReference F0(
        List<UniversalVoiceSegment> members, string domain, List<SharedFeatureSkip> skipped)
    {
        var usable = new List<UniversalVoiceSegment>();
        foreach (var segment in members)
        {
            if (double.IsFinite(segment.MeanF0Hz) && segment.MeanF0Hz > 0 &&
                double.IsFinite(segment.F0StdDevHz) && segment.F0StdDevHz >= 0 &&
                double.IsFinite(segment.PitchReliability) && segment.PitchReliability > 0)
                usable.Add(segment);
            else
                skipped.Add(new(segment.SegmentId, SharedFeatureName.F0Distribution, domain, "f0_unavailable"));
        }
        if (usable.Count == 0) return Unavailable(SharedFeatureName.F0Distribution, domain);
        var mean = usable.Average(x => x.MeanF0Hz);
        var variance = usable.Average(x => x.F0StdDevHz * x.F0StdDevHz + (x.MeanF0Hz - mean) * (x.MeanF0Hz - mean));
        return Measured(SharedFeatureName.F0Distribution, domain, usable, scalars: new Dictionary<string, double>
        {
            ["mean_f0_hz"] = mean, ["f0_std_dev_hz"] = Math.Sqrt(variance)
        });
    }

    // style/prosodyは4項目すべてが0の区間を「未観測」として使わない。
    private static SharedFeatureReference Style(
        List<UniversalVoiceSegment> members, string domain, List<SharedFeatureSkip> skipped)
    {
        var usable = new List<UniversalVoiceSegment>();
        foreach (var segment in members)
        {
            var s = segment.StyleProsody;
            var values = new[] { s.SpeakingRate, s.PitchRangeSemitones, s.EnergyVariation, s.PauseRatio };
            if (values.All(double.IsFinite) && values.Any(x => x != 0)) usable.Add(segment);
            else skipped.Add(new(segment.SegmentId, SharedFeatureName.StyleProsody, domain, "style_unavailable"));
        }
        if (usable.Count == 0) return Unavailable(SharedFeatureName.StyleProsody, domain);
        return Measured(SharedFeatureName.StyleProsody, domain, usable, scalars: new Dictionary<string, double>
        {
            ["speaking_rate"] = usable.Average(x => x.StyleProsody.SpeakingRate),
            ["pitch_range_semitones"] = usable.Average(x => x.StyleProsody.PitchRangeSemitones),
            ["energy_variation"] = usable.Average(x => x.StyleProsody.EnergyVariation),
            ["pause_ratio"] = usable.Average(x => x.StyleProsody.PauseRatio)
        });
    }

    private static SharedFeatureReference Measured(
        string feature, string domain, List<UniversalVoiceSegment> usable,
        IReadOnlyList<double>? vector = null, IReadOnlyDictionary<string, double>? scalars = null) => new()
    {
        Feature = feature, Domain = domain, Status = ConfidenceStatus.Measured,
        SegmentIds = usable.Select(x => x.SegmentId).Order(StringComparer.Ordinal).ToArray(),
        Vector = vector, Scalars = scalars, MeasurementVersion = MeasurementVersion
    };

    private static SharedFeatureReference Unavailable(string feature, string domain) => new()
    {
        Feature = feature, Domain = domain, Status = ConfidenceStatus.Unavailable, SegmentIds = [],
        MeasurementVersion = MeasurementVersion
    };
}
