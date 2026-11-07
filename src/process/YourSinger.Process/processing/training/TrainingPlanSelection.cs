using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TextureChoice: 画面の質感選択肢1件(登録簿の1プリセット)]
//   フィールド: [PresetId/Version: 保存する識別, Label: 名前と実現状態, Description: 説明と、実現できない場合の理由]
// }
public sealed record TextureChoice(string PresetId, string Version, string Label, string Description);

// {
//   責務: [TrainingPlanSelection: 画面で選んだ質感・初期化モードを、ジョブ作成の用途別設定へ変換し、解決済み計画を日本語で説明する]
//   処理: [1: 用途(talk/singing)に対応する質感だけを選択肢にする 2: 選択をそのまま設定へ保存する 3: 解決済み計画の各段階の初期化方式・警告・停止理由を日本語にする]
//   補足: [
//     選んだ質感と初期化モードは、対応外・実現できない場合でも別の方式へ黙って変えない。実現状態は表示だけで選択を妨げない。
//     初期化モード(自動/追加学習/最初から新規学習)は高度な設定で、質感より先に要求しない。
//   ]
// }
public static class TrainingPlanSelection
{
    // {
    //   責務: [Choices: 用途に対応する質感の選択肢を作る]
    //   引数: [registry: 質感登録簿, target: talk または singing]
    //   戻り値: [choices: 登録簿の列挙順の選択肢]
    // }
    public static IReadOnlyList<TextureChoice> Choices(TexturePresetRegistry registry, string target) =>
        registry.Entries.Where(x => x.Preset.SupportedTargets.Contains(target)).Select(x =>
        {
            var state = StatusLabel(x.Status);
            var description = x.Status == TexturePresetStatus.Available
                ? x.Preset.DescriptionJa
                : $"{x.Preset.DescriptionJa} ({x.Reason})";
            return new TextureChoice(x.Preset.PresetId, x.Preset.Version, $"{x.Preset.DisplayNameJa}({state})", description);
        }).ToArray();

    // {
    //   責務: [BuildOptions: 選んだ質感とモードをジョブ作成の設定にする]
    //   引数: [choice: 選んだ質感(未選択は null で既定のまま), mode: 初期化モード]
    //   戻り値: [options: 質感もモードも既定なら null(従来どおり既定で作成)]
    // }
    public static TrainingJobOptions? BuildOptions(TextureChoice? choice, TrainingMode mode)
    {
        if (choice is null && mode == TrainingMode.Auto)
            return null;
        return new TrainingJobOptions
        {
            Mode = mode,
            Texture = choice is null ? null : new TrainingTextureRequest { Preset = choice.PresetId, PresetVersion = choice.Version }
        };
    }

    // {
    //   責務: [DescribeJob: ジョブに保存された質感・初期化モードの指定と、解決済み計画を日本語の行にする]
    //   引数: [job: 学習ジョブ]
    //   戻り値: [lines: 指定の行と、計画があればその説明。旧ジョブは「指定なし」と明記する]
    // }
    public static IReadOnlyList<string> DescribeJob(TrainingJobRecord job)
    {
        var lines = new List<string>();
        if (job.IsLegacyWithoutTrainingContract)
            lines.Add("  学習方式の指定: なし(旧形式のジョブ。再作成すると既定で作り直します)");
        else
            lines.Add($"  質感: {job.RequestedTexture?.Preset ?? TrainingJobVocabulary.DefaultTexturePreset} / 初期化モード: {ModeLabel(job.RequestedTrainingMode!.Value)}");
        if (job.ResolvedPlan is { } plan)
            lines.AddRange(DescribePlan(plan).Select(x => "  " + x));
        else
            lines.Add("  計画: 未解決(学習を始めるときに確認します)");
        return lines;
    }

    public static string ModeLabel(TrainingMode mode) => mode switch
    {
        TrainingMode.FineTune => "追加学習",
        TrainingMode.FromScratch => "最初から新規学習",
        _ => "自動"
    };

    // {
    //   責務: [DescribePlan: 解決済み計画を、どの工程をどの初期化方式で行うか・警告・停止理由として日本語にする]
    //   引数: [plan: 解決済み計画]
    //   戻り値: [lines: 説明行(実行可否、各工程、警告、停止理由)]
    // }
    public static IReadOnlyList<string> DescribePlan(TrainingResolvedPlanRecord plan)
    {
        var lines = new List<string> { plan.Executable ? "この計画は実行できます。" : "この計画は実行できません。" };
        foreach (var stage in plan.Stages)
            lines.Add($"工程 {stage.StageId}: {StageModeLabel(stage.Mode)} / 初期化: {InitializationLabel(stage.Initialization)}");
        lines.AddRange(plan.Warnings.Select(x => "注意: " + x));
        lines.AddRange(plan.StopReasons.Select(x => "停止理由: " + x));
        return lines;
    }

    private static string StageModeLabel(string mode) => mode switch
    {
        "fine_tune" => "追加学習",
        "from_scratch" => "最初から新規学習",
        _ => mode
    };

    private static string InitializationLabel(TrainingInitialization? initialization) => initialization?.Type switch
    {
        TrainingJobVocabulary.PretrainedCheckpoint =>
            $"学習済み重み({string.Join("、", initialization.Components.Select(x => x.Name))})",
        TrainingJobVocabulary.RandomInitialization => "ランダム初期化(新規)",
        _ => "なし(この工程は初期化を使いません)"
    };

    private static string StatusLabel(string status) => status switch
    {
        TexturePresetStatus.Available => "対応",
        TexturePresetStatus.Partial => "一部対応",
        TexturePresetStatus.Unavailable => "現在は不可",
        TexturePresetStatus.Unsupported => "未対応",
        _ => "無効"
    };
}
