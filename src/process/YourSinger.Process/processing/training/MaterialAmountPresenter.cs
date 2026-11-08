using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [MaterialAmountPresenter: 話者の素材量(発話教師・歌唱教師・声質参照)を、測定状況つきの日本語で説明する]
//   処理: [1: 3つの軸を別々の行にする 2: 秒数を分秒に直す 3: 測定できていない軸は「一部未測定/未測定」を添え、0と読み替えない]
//   補足: [「足りる/足りない」は判断しない(それは学習方式の入力要件の役割)。教師が無い軸は「なし」と書き、別の軸で代用したとは書かない。]
// }
public static class MaterialAmountPresenter
{
    // {
    //   責務: [Describe: 棚卸しを日本語の行にする]
    //   引数: [inventory: 話者の棚卸し]
    //   戻り値: [lines: 発話・歌唱・声質参照の素材量と測定状況の行]
    // }
    public static IReadOnlyList<string> Describe(SpeakerDataInventory inventory)
    {
        var status = inventory.MeasurementStatus;
        var lines = new List<string>
        {
            $"話者 {inventory.SpeakerId} の素材量(長さは区間の音声ファイルから読んだ値です)",
            $"発話教師: {Amount(inventory.Speech.EligibleSeconds, inventory.Speech.RawSeconds)} / 独立した発話 {inventory.Speech.IndependentUtterances}件" +
                $" / 書き起こし利用可 {inventory.Speech.TranscriptUsableCount}件{Note(status, "transcription", "書き起こしの確信度")}",
            $"歌唱教師: {Amount(inventory.Singing.EligibleSeconds, inventory.Singing.RawSeconds)} / 独立したフレーズ {inventory.Singing.IndependentPhrases}件" +
                $" / 音域 {inventory.Singing.PitchBinsObserved}区分で観測{Note(status, "singing_f0", "音高")}",
            $"声質参照: {Amount(inventory.IdentityReference.EligibleSeconds, null)} / 元の録音 {inventory.IdentityReference.DistinctSources}件" +
                $"{Note(status, "speaker_identity", "話者の特徴")}(教師にならない素材も、本人の声質の参考にできます)"
        };
        if (Note(status, "duration", "区間の長さ") is { Length: > 0 } duration)
            lines.Add($"注意: {duration.TrimStart(' ', '/')}。測れなかった区間は0秒として扱わず、秒数に含めていません。");
        return lines;
    }

    // 適格な秒数と全体の秒数。適格が0なら「なし」と書き、全体があれば添える。
    private static string Amount(double eligible, double? raw)
    {
        var main = eligible <= 0 ? "なし" : Format(eligible);
        return raw is { } r && r > 0 ? $"{main}(割り当て済みの全体 {Format(r)})" : main;
    }

    private static string Format(double seconds)
    {
        var whole = (int)Math.Round(seconds);
        return whole < 60 ? $"{whole}秒" : $"{whole / 60}分{whole % 60:00}秒";
    }

    // 測定状況が measured 以外なら注記する。キーが無い場合も測定済みと見なさない。
    private static string Note(IReadOnlyDictionary<string, string> status, string key, string label) =>
        status.TryGetValue(key, out var value) && value == ConfidenceStatus.Measured
            ? ""
            : value == "partially_measured" ? $" / {label}は一部未測定" : $" / {label}は未測定";
}
