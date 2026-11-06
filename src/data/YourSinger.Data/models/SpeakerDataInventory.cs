namespace YourSinger.Data.Models;

// 話者ごとの発話教師・歌唱教師・声質参照の実効データ量(別軸)。学習方式やBackendは決めない。
// 正本: docs/design/v1-training/learning-system-design.md「4. Basic Source Profile」。
// 閾値(「足りる/足りない」)は持たない。それは各学習戦略の input_requirements に属する。
public sealed class SpeakerDataInventory
{
    public const string CurrentSchemaVersion = "1";

    public required string SchemaVersion { get; init; }
    public required string SpeakerId { get; init; }
    public required string DatasetFingerprint { get; init; }
    // 集計規則の版。規則を変えたら上げる(キャッシュ無効化に使う)。
    public required string CalculationVersion { get; init; }
    // 素材・手動修正・規則版・集計結果から作る決定的なhash(キャッシュ・計画の入力)。
    public required string InventoryRevision { get; init; }
    public required SpeechInventory Speech { get; init; }
    public required SingingInventory Singing { get; init; }
    public required IdentityReferenceInventory IdentityReference { get; init; }
    // 指標の測定状況。measured / partially_measured / unavailable。測れない値を0と読み替えない。
    public required Dictionary<string, string> MeasurementStatus { get; init; }
    // 閾値を使わない導出タグ(has_speech_teacher など)。「多い/少ない」は戦略側が判断する。
    public required List<string> DerivedTags { get; init; }
}

public sealed class SpeechInventory
{
    // 話者に割り当てられた発話区間の全秒数(適格性に関係なく)。
    public required double RawSeconds { get; init; }
    // 発話教師として適格な区間の秒数。
    public required double EligibleSeconds { get; init; }
    public required int DistinctRecordings { get; init; }
    // 同じ原音(CacheKey)を重複して数えない独立した発話数。
    public required int IndependentUtterances { get; init; }
    public required int TranscriptUsableCount { get; init; }
    public required int PhonemeCoverageCount { get; init; }
}

public sealed class SingingInventory
{
    public required double RawSeconds { get; init; }
    // 検証済み音素境界を持つ、歌唱教師として適格な区間の秒数。
    public required double EligibleSeconds { get; init; }
    public required int DistinctRecordings { get; init; }
    public required int IndependentPhrases { get; init; }
    // 6半音幅の音域binのうち、実測のF0で観測した数。
    public required int PitchBinsObserved { get; init; }
}

public sealed class IdentityReferenceInventory
{
    // 教師にならなくても本人の声質参照に使える区間の秒数。品質が低いだけでは除かない。
    public required double EligibleSeconds { get; init; }
    public required int DistinctSources { get; init; }
}
