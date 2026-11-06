using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Talk;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 声質参照(経路T1)をTrainerへ渡す解決と、利用記録の確定条件を確認する。実音声での評価は別(未実施)。
public sealed class StyleBertVits2TimbreUsageTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ys-timbre-usage");

    private static TalkTimbreAdaptationInput Input(params string[] paths) => new()
    {
        SpeakerId = "spk_a",
        References = [.. paths.Select((p, i) => new TalkTimbreReference($"s{i}", "src", p))],
        Skipped = [],
        Usage = new SharedFeatureUsageEntry
        {
            Feature = "style_vector_from_singing_audio", Domain = SharedFeatureDomain.Singing,
            Support = SharedFeatureSupport.Supported, Used = paths.Length > 0, Reason = "予定"
        }
    };

    private static TrainingJobRecord Job() => new()
    {
        JobId = "job", SpeakerId = "spk_a", Target = TrainingTarget.Talk, DatasetFingerprint = "fp"
    };

    [Fact]
    public void References_AreResolvedInsideTheWorkspace()
    {
        var resolved = StyleBertVits2TrainerService.ResolveTimbreReferences(
            new ProjectWorkspace(Root), Input("segments/a.wav"));
        Assert.Equal([Path.Combine(Path.GetFullPath(Root), "segments", "a.wav")], resolved);
        Assert.Empty(StyleBertVits2TrainerService.ResolveTimbreReferences(new ProjectWorkspace(Root), null));
    }

    [Fact]
    public void ReferenceOutsideTheWorkspace_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => StyleBertVits2TrainerService.ResolveTimbreReferences(
            new ProjectWorkspace(Root), Input("../outside.wav")));
    }

    [Fact]
    public void Usage_IsRecordedAsUsedOnlyWhenTheTrainerReportsTheStyle()
    {
        var job = Job();
        var result = JsonDocument.Parse("""{"log_path":"x","timbre_style":{"reference_count":3}}""").RootElement;
        StyleBertVits2TrainerService.RecordTimbreUsage(job, Input("a.wav"), ["a.wav"], result);
        var entry = Assert.Single(job.SharedFeatureUsage!);
        Assert.True(entry.Used);
        Assert.Contains("3", entry.Reason);
    }

    [Fact]
    public void ReferencesSentButNotReported_FailsInsteadOfClaimingUse()
    {
        var result = JsonDocument.Parse("""{"log_path":"x"}""").RootElement;
        Assert.Throws<InvalidOperationException>(() =>
            StyleBertVits2TrainerService.RecordTimbreUsage(Job(), Input("a.wav"), ["a.wav"], result));
    }

    [Fact]
    public void NoReferences_RecordsUnusedAndReplacesAnEarlierEntry()
    {
        var job = Job();
        job.SharedFeatureUsage = [Input("a.wav").Usage];
        var result = JsonDocument.Parse("""{"log_path":"x"}""").RootElement;
        StyleBertVits2TrainerService.RecordTimbreUsage(job, Input(), [], result);
        Assert.False(Assert.Single(job.SharedFeatureUsage).Used);
    }
}
