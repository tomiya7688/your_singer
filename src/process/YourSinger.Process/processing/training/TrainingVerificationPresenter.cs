using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingVerificationPresenter: 学習後の検証状態を、工程ごとに別々の日本語で説明する]
//   処理: [1: ジョブ状態と検証記録から総合区分を決める 2: 学習・checkpoint・生成物・推論・外部互換・品質を別行にする 3: checkpoint候補と選定理由、試験音声の再生可否を示す]
//   補足: [
//     「利用可能」は checkpoint・export・inference がすべて passed で、failed/warning/不明が無い場合だけ。
//     not_run・unavailable・記録なし・不正な記録は passed として扱わない。外部互換は総合区分と別行で必ず示す。
//   ]
// }
public static class TrainingVerificationPresenter
{
    private static readonly (string Key, string Label)[] StageOrder =
    [
        ("checkpoint", "checkpoint検査"), ("export", "出力物の構築"), ("inference", "推論試験(実音声生成)"),
        ("external_compatibility", "外部互換"), ("quality", "品質評価")
    ];

    // {
    //   責務: [Overall: 総合区分を返す]
    //   引数: [state: 学習ジョブの状態, load: 検証記録の読み取り結果]
    //   戻り値: [label: 学習中/学習完了・検証待ち/生成試験中/利用可能/注意あり/検証失敗 などの区分]
    // }
    public static string Overall(TrainingJobState state, TrainingVerificationLoad load)
    {
        switch (state)
        {
            case TrainingJobState.Pending: return "待機中";
            case TrainingJobState.Running: return "学習中";
            case TrainingJobState.Failed: return "学習失敗";
            case TrainingJobState.Canceled: return "学習中止";
            case TrainingJobState.Succeeded: break;
            default: return "不明";
        }
        if (load.Status == VerificationLoadStatus.Missing) return "学習完了・未検証";
        if (load.Status == VerificationLoadStatus.Invalid || load.Record is null) return "学習完了・検証記録を確認できません(未検証)";
        var stages = load.Record.Stages;
        string StateOf(string key) => stages.TryGetValue(key, out var s) ? s.State : "not_run";
        var core = new[] { "checkpoint", "export", "inference" };
        if (core.Any(k => StateOf(k) == "failed") || StateOf("quality") == "failed") return "検証失敗";
        if (StateOf("inference") == "running") return "生成試験中";
        if (core.Any(k => StateOf(k) != "passed" && StateOf(k) != "warning")) return "学習完了・検証待ち";
        var anyWarning = core.Concat(["quality", "external_compatibility"]).Any(k => StateOf(k) is "warning" or "failed")
            || stages.Values.Any(s => !KnownStates.Contains(s.State));
        return anyWarning ? "注意あり" : "利用可能";
    }

    private static readonly HashSet<string> KnownStates = ["not_run", "running", "passed", "warning", "failed", "unavailable"];

    // {
    //   責務: [Describe: ジョブ1件の検証状態を行にする]
    //   引数: [state: 学習ジョブの状態, load: 検証記録の読み取り結果, samples: 試験音声の一覧]
    //   戻り値: [lines: 総合区分・工程別状態・checkpoint選定・試験音声の日本語行]
    // }
    public static IReadOnlyList<string> Describe(TrainingJobState state, TrainingVerificationLoad load, IReadOnlyList<VerificationSample> samples)
    {
        var lines = new List<string>
        {
            $"総合: {Overall(state, load)}",
            $"学習: {TrainingStateLabel(state)}(学習器が終了した事実で、音声が作れる保証ではありません)"
        };
        if (load.Status == VerificationLoadStatus.Missing)
        {
            lines.Add("検証: 記録がありません。検証は未実施として扱い、利用可能とは表示しません。");
            return lines;
        }
        if (load.Status == VerificationLoadStatus.Invalid || load.Record is null)
        {
            lines.Add($"検証: {load.Message ?? "記録を確認できません。"}未検証として扱います。");
            return lines;
        }
        foreach (var (key, label) in StageOrder)
        {
            if (!load.Record.Stages.TryGetValue(key, out var stage))
            {
                lines.Add($"{label}: 未実施");
                continue;
            }
            var reasons = stage.Reasons.Count == 0 ? "" : $"({string.Join("、", stage.Reasons.Select(ReasonLabel))})";
            lines.Add($"{label}: {StageLabel(stage.State)}{reasons}");
        }
        if (load.Record.CompatibilityTarget is { Length: > 0 } target)
            lines.Add($"外部互換の対象: {target}(実機で確認していない場合は未実施と表示します)");
        lines.AddRange(DescribeSelection(load.Record.CheckpointSelection));
        if (samples.Count == 0) lines.Add("試験音声: 保存されていません。");
        foreach (var sample in samples) lines.Add($"試験音声: {sample.Label} / {SampleLabel(sample.Status)}");
        return lines;
    }

    // {
    //   責務: [DescribeSelection: checkpoint候補と選定理由を行にする]
    //   処理: [記録なし・pending・選定IDが候補に無い場合は「選定保留」とし、1位を付けない]
    //   引数: [selection: 選定の記録]
    //   戻り値: [lines: 日本語行]
    // }
    public static IReadOnlyList<string> DescribeSelection(CheckpointSelectionRecord? selection)
    {
        if (selection is null || selection.State != "selected" || selection.Candidates.All(x => x.Id != selection.SelectedId))
        {
            var pending = new List<string> { "checkpoint選定: 保留(未評価のため、どの候補も自動選択していません。比較試聴で選んでください)" };
            if (selection is not null)
                pending.AddRange(selection.Candidates.Select(c => $"  候補 {c.Id}: {(c.Eligible ? "適格" : "不適格")}{Suffix(c.Reason)}"));
            return pending;
        }
        var lines = new List<string> { $"checkpoint選定: {selection.SelectedId}{Suffix(selection.Reason, " / 選定理由: ")}" };
        lines.AddRange(selection.Candidates.Select(c => $"  候補 {c.Id}: {(c.Eligible ? "適格" : "不適格")}{Suffix(c.Reason)}"));
        return lines;
    }

    private static string Suffix(string? text, string prefix = " / ") => string.IsNullOrWhiteSpace(text) ? "" : prefix + text;

    private static string TrainingStateLabel(TrainingJobState state) => state switch
    {
        TrainingJobState.Pending => "待機中",
        TrainingJobState.Running => "実行中",
        TrainingJobState.Succeeded => "完了",
        TrainingJobState.Failed => "失敗",
        TrainingJobState.Canceled => "中止",
        _ => "不明"
    };

    private static string StageLabel(string state) => state switch
    {
        "passed" => "合格",
        "warning" => "注意あり",
        "failed" => "失敗",
        "running" => "実行中",
        "not_run" => "未実施",
        "unavailable" => "利用不可(測定できないため合格にしません)",
        _ => $"不明な状態({state})"
    };

    private static string ReasonLabel(string code) => code switch
    {
        "runner_unavailable" => "実行環境がありません",
        "low_intelligibility" => "聞き取りやすさが低め",
        "silent_output" => "ほぼ無音",
        "non_finite" => "異常な値を含む",
        "decode_failed" => "音声として読めません",
        _ => code
    };

    private static string SampleLabel(SampleStatus status) => status switch
    {
        SampleStatus.Playable => "試聴できます",
        SampleStatus.HashNotRecorded => "試聴できます(保存時の確認用ハッシュが無く、内容の一致は未確認)",
        SampleStatus.FileMissing => "ファイルがありません(再生できません)",
        SampleStatus.HashMismatch => "保存後に内容が変わっています(再生しません)",
        SampleStatus.OutsideVerificationFolder => "検証フォルダの外を指しています(開きません)",
        _ => "不明"
    };

    // {
    //   責務: [IsPlayable: 試聴ボタンで開いてよい音声か]
    //   引数: [sample: 試験音声]
    //   戻り値: [playable: 再生可能な場合 true]
    // }
    public static bool IsPlayable(VerificationSample sample) =>
        sample.Status is SampleStatus.Playable or SampleStatus.HashNotRecorded;
}
