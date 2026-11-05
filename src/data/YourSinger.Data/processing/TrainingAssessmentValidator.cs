using YourSinger.Data.Models;

namespace YourSinger.Data.Processing;

// 学習用Assessmentのスキーマ検証。違反は日本語の原因付き InvalidDataException で返す。
public static class TrainingAssessmentValidator
{
    // {
    //   責務: [RequireSafeComponent: ID・ジョブ名がパスの1要素として安全か検証する]
    //   処理: [1: 空・"."・".."・制御文字・区切り・予約文字・末尾の"."/空白を拒否する]
    //   戻り値: [value: 検証済みの同じ文字列]
    // }
    public static string RequireSafeComponent(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Any(x => char.IsControl(x) || "<>:\"/\\|?*".Contains(x)) ||
            value.EndsWith('.') || value.EndsWith(' '))
            throw Invalid($"{label}に使用できない文字があります(領域外のパスを指す可能性があります)。");
        return value;
    }

    // {
    //   責務: [Validate: record全体が契約に合うか検証する]
    //   処理: [
    //     1: ヘッダ(schema・policy・ID・target・fingerprint)を検証する
    //     2: itemsの重複・昇順・各項目を検証する
    //   ]
    //   戻り値: [なし。違反時は InvalidDataException]
    // }
    public static void Validate(TrainingAssessmentRecord? record)
    {
        if (record is null) throw Invalid("Assessmentが空です。");
        if (record.SchemaVersion != TrainingAssessmentRecord.CurrentSchemaVersion)
            throw Invalid($"schema_version '{record.SchemaVersion}' には対応していません(対応: {TrainingAssessmentRecord.CurrentSchemaVersion})。");
        RequireText(record.PolicyVersion, "policy_version");
        RequireSafeComponent(record.JobId, "job_id");
        RequireText(record.SpeakerId, "speaker_id");
        RequireOneOf(record.Target, TrainingAssessmentVocabulary.Targets, "target");
        RequireText(record.SourceDatasetFingerprint, "source_dataset_fingerprint");
        if (record.ManualOverrideRevision is { } revision) RequireText(revision, "manual_override_revision");
        if (record.Items is null) throw Invalid("items がありません。");

        string? previous = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in record.Items)
        {
            if (item is null) throw Invalid("items に null があります。");
            RequireText(item.SegmentId, "segment_id");
            if (!seen.Add(item.SegmentId)) throw Invalid($"segment_id '{item.SegmentId}' が重複しています。");
            if (previous is not null && string.CompareOrdinal(previous, item.SegmentId) > 0)
                throw Invalid($"items が segment_id 昇順ではありません('{previous}' の後に '{item.SegmentId}')。");
            previous = item.SegmentId;
            ValidateItem(item);
        }
    }

    private static void ValidateItem(TrainingAssessmentItem item)
    {
        var id = item.SegmentId;
        if (item.Role is { } role) RequireOneOf(role, TrainingAssessmentVocabulary.Roles, $"{id}: role");
        RequireOneOf(item.Eligibility, TrainingAssessmentVocabulary.Eligibilities, $"{id}: eligibility");
        RequireReasonCodes(item.ReasonCodes, $"{id}: reason_codes");
        if (item.Quality is null) throw Invalid($"{id}: quality がありません。");
        if (item.Utility is null) throw Invalid($"{id}: utility がありません。");
        RequireMeasurement(item.Quality.Value, item.Quality.Status, $"{id}: quality");
        if (item.Quality.Components is null) throw Invalid($"{id}: quality.components がありません。");
        foreach (var (name, component) in item.Quality.Components)
        {
            RequireText(name, $"{id}: quality.components の名前");
            if (component is null) throw Invalid($"{id}: quality.components.{name} が null です。");
            RequireMeasurement(component.Value, component.Status, $"{id}: quality.components.{name}");
        }
        RequireMeasurement(item.Utility.Value, item.Utility.Status, $"{id}: utility");
        RequireReasonCodes(item.Utility.ReasonCodes, $"{id}: utility.reason_codes");
        RequireWeight(item.RawWeight, $"{id}: raw_weight");
        RequireWeight(item.TrainingWeight, $"{id}: training_weight");
        var eligible = item.Eligibility == "eligible";
        if (eligible && item.TrainingWeight <= 0)
            throw Invalid($"{id}: eligible の training_weight は正の値が必要です。");
        if (!eligible && item.TrainingWeight != 0)
            throw Invalid($"{id}: {item.Eligibility} の training_weight は 0 が必要です。");
    }

    private static void RequireMeasurement(double? value, string? status, string label)
    {
        RequireOneOf(status, TrainingAssessmentVocabulary.Statuses, $"{label}.status");
        if (value is { } v && (!double.IsFinite(v) || v < 0 || v > 1))
            throw Invalid($"{label}.value は 0〜1 の有限数または null が必要です(値: {v})。");
        if (status == ConfidenceStatus.Measured && value is null)
            throw Invalid($"{label} は measured ですが value が null です(測定済みは値が必要です)。");
        if (status == ConfidenceStatus.Unavailable && value is not null)
            throw Invalid($"{label} は unavailable ですが value が null ではありません。");
    }

    private static void RequireReasonCodes(List<string>? codes, string label)
    {
        if (codes is null) throw Invalid($"{label} がありません。");
        foreach (var code in codes)
            RequireOneOf(code, TrainingAssessmentVocabulary.ReasonCodes, label);
        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
            throw Invalid($"{label} に重複があります。");
    }

    private static void RequireWeight(double value, string label)
    {
        if (!double.IsFinite(value) || value < 0)
            throw Invalid($"{label} は 0 以上の有限数が必要です(値: {value})。");
    }

    private static void RequireText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{label} が空です。");
    }

    private static void RequireOneOf(string? value, IReadOnlyList<string> allowed, string label)
    {
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
            throw Invalid($"{label} '{value}' は不正です(許可: {string.Join(", ", allowed)})。");
    }

    internal static InvalidDataException Invalid(string cause, Exception? inner = null) =>
        new($"学習用Assessmentが不正です: {cause}", inner);
}
