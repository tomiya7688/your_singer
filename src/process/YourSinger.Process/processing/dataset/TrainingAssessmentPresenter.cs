using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [TrainingAssessmentCounts: 学習用の区間評価を、画面に出す4区分の件数にまとめた結果]
//   フィールド: [Target: talk/singing, Adopted: 通常採用, LowQualityAdopted: 低品質だが重みを持って採用, NeedsReview: 要確認, Excluded: 除外]
// }
public sealed record TrainingAssessmentCounts(string Target, int Adopted, int LowQualityAdopted, int NeedsReview, int Excluded);

// {
//   責務: [TrainingAssessmentPresenter: 区間評価(適格性・Q/U・重み・reason_codes)を日本語の件数と説明に整形する]
//   処理: [1: 用途別に採用・低品質採用・要確認・除外へ分類する 2: 区間のQ/U/重みと理由を日本語1行にする]
//   補足: [
//     未計測・未校正の値は0%品質と見せず「未計測」「未校正」と書く。
//     低品質は Q が measured で中立値(0.5)未満のものに限る。品質だけでは除外せず、eligible なら重みを持って採用に数える。
//   ]
// }
public static class TrainingAssessmentPresenter
{
    private const double LowQualityBelow = 0.5;

    private static readonly Dictionary<string, string> ReasonText = new()
    {
        ["user_excluded"] = "利用者が除外",
        ["wrong_speaker"] = "別の話者",
        ["unreadable_audio"] = "音声を読み込めない",
        ["no_usable_voice"] = "使える声がない",
        ["wrong_content_type"] = "用途に合わない種類",
        ["alignment_required"] = "音素の対応付けが必要",
        ["rare_phoneme"] = "珍しい音素を含む",
        ["speaker_unconfirmed"] = "話者が未確認"
    };

    // {
    //   責務: [Count: 区間評価を4区分の件数にまとめる]
    //   引数: [record: 1用途分の区間評価]
    //   戻り値: [counts: 用途と各区分の件数]
    // }
    public static TrainingAssessmentCounts Count(TrainingAssessmentRecord record)
    {
        int adopted = 0, low = 0, review = 0, excluded = 0;
        foreach (var item in record.Items)
        {
            switch (Classify(item))
            {
                case Category.Adopted: adopted++; break;
                case Category.LowQualityAdopted: low++; break;
                case Category.NeedsReview: review++; break;
                default: excluded++; break;
            }
        }
        return new TrainingAssessmentCounts(record.Target, adopted, low, review, excluded);
    }

    // {
    //   責務: [Summarize: 1用途分の件数を日本語1行にする]
    //   引数: [counts: Countの結果]
    //   戻り値: [line: 「歌唱: 採用 N / 低品質だが採用 N / 要確認 N / 除外 N」]
    // }
    public static string Summarize(TrainingAssessmentCounts counts) =>
        $"{(counts.Target == "singing" ? "歌唱" : "トーク")}: 採用 {counts.Adopted}件 / 低品質だが採用 {counts.LowQualityAdopted}件 / 要確認 {counts.NeedsReview}件 / 除外 {counts.Excluded}件";

    // {
    //   責務: [Describe: 1区間のQ/U/重みと主な理由を日本語1行にする]
    //   引数: [item: 区間の評価]
    //   戻り値: [line: 区分・品質・有用度・重み・理由]
    // }
    public static string Describe(TrainingAssessmentItem item)
    {
        var category = Classify(item) switch
        {
            Category.Adopted => "採用",
            Category.LowQualityAdopted => "低品質だが採用",
            Category.NeedsReview => "要確認",
            _ => "除外"
        };
        var reasons = item.ReasonCodes.Concat(item.Utility.ReasonCodes).Distinct()
            .Select(code => ReasonText.GetValueOrDefault(code, code)).ToList();
        var reasonText = reasons.Count == 0 ? "" : $" / 理由: {string.Join("、", reasons)}";
        return $"{item.SegmentId}: {category} / 品質 {Measure(item.Quality.Value, item.Quality.Status)} / 有用度 {Measure(item.Utility.Value, item.Utility.Status)} / 重み {item.TrainingWeight:0.00}{reasonText}";
    }

    // 未計測・未校正などは数値にせず状態名で示す(0%と誤解させない)。
    private static string Measure(double? value, string status) => status switch
    {
        ConfidenceStatus.Measured when value is { } v => $"{v * 100:0}%",
        ConfidenceStatus.Uncalibrated => "未校正",
        ConfidenceStatus.Unavailable => "未計測",
        ConfidenceStatus.LegacyUnknown => "旧形式(不明)",
        _ => "無効"
    };

    private static Category Classify(TrainingAssessmentItem item)
    {
        if (item.Eligibility == "excluded") return Category.Excluded;
        if (item.Eligibility == "needs_review") return Category.NeedsReview;
        var lowQuality = item.Quality.Status == ConfidenceStatus.Measured && item.Quality.Value is { } q && q < LowQualityBelow;
        return lowQuality && item.TrainingWeight > 0 ? Category.LowQualityAdopted : Category.Adopted;
    }

    private enum Category { Adopted, LowQualityAdopted, NeedsReview, Excluded }
}
