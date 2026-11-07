using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [TrainingPlanSelectionTests: 質感の選択肢・設定への変換・解決済み計画の日本語説明を確認する] }
public sealed class TrainingPlanSelectionTests
{
    [Fact]
    public void ChoicesAreLimitedToThePurposeAndShowUnsupportedStateWithoutHidingThem()
    {
        var registry = new TexturePresetRegistry();
        var talk = TrainingPlanSelection.Choices(registry, "talk");
        Assert.NotEmpty(talk);
        Assert.Contains(talk, x => x.PresetId == "builtin.natural");
        // 実現工程が無い質感も選択肢に残し、未対応と理由を示す(黙って別の質感に変えない)。
        var natural = talk.Single(x => x.PresetId == "builtin.natural");
        Assert.Contains("未対応", natural.Label);
        Assert.Contains("効果は保証されません", natural.Description);
        Assert.Empty(TrainingPlanSelection.Choices(registry, "other"));
    }

    [Fact]
    public void DefaultSelectionStaysNullAndAChoiceIsKeptExactly()
    {
        Assert.Null(TrainingPlanSelection.BuildOptions(null, TrainingMode.Auto));
        var choice = new TextureChoice("builtin.powerful", "1.0.0", "力強い", "説明");
        var options = TrainingPlanSelection.BuildOptions(choice, TrainingMode.FromScratch)!;
        Assert.Equal("builtin.powerful", options.Texture!.Preset);
        Assert.Equal("1.0.0", options.Texture.PresetVersion);
        Assert.Equal(TrainingMode.FromScratch, options.Mode);
        Assert.Equal(TrainingMode.FineTune, TrainingPlanSelection.BuildOptions(null, TrainingMode.FineTune)!.Mode);
        Assert.Equal("最初から新規学習", TrainingPlanSelection.ModeLabel(TrainingMode.FromScratch));
    }

    [Fact]
    public void ThePlanIsDescribedPerStageWithInitializationWarningsAndStopReasons()
    {
        var plan = new TrainingResolvedPlanRecord
        {
            ManifestHash = "h",
            InventoryRevision = "r",
            Executable = false,
            Stages =
            [
                new TrainingResolvedStageRecord
                {
                    StageId = "acoustic", OperationId = "o", Mode = "fine_tune",
                    Initialization = new TrainingInitialization
                    {
                        Type = TrainingJobVocabulary.PretrainedCheckpoint,
                        Components = [new TrainingInitializationComponent { Name = "base", Sha256 = "s", Path = "p" }]
                    }
                },
                new TrainingResolvedStageRecord
                {
                    StageId = "duration", OperationId = "o", Mode = "from_scratch",
                    Initialization = new TrainingInitialization { Type = TrainingJobVocabulary.RandomInitialization }
                }
            ],
            Warnings = ["素材が少なめです"],
            StopReasons = ["GPUが見つかりません"]
        };
        var text = string.Join("\n", TrainingPlanSelection.DescribePlan(plan));
        Assert.Contains("実行できません", text);
        Assert.Contains("acoustic: 追加学習 / 初期化: 学習済み重み(base)", text);
        Assert.Contains("duration: 最初から新規学習 / 初期化: ランダム初期化(新規)", text);
        Assert.Contains("注意: 素材が少なめです", text);
        Assert.Contains("停止理由: GPUが見つかりません", text);
    }
}
