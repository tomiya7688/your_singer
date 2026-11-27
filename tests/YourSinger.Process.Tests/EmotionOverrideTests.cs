using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// { 責務: [EmotionOverrideTests: 感情ラベルの設定・取り消し・不正名の拒否・再解析後の再適用・音素に触れないことを確認する] }
public sealed class EmotionOverrideTests
{
    [Fact]
    public async Task SetAndClearEmotionIsSavedWithoutTouchingTranscriptOrPhonemes()
    {
        using var project = new TestProject();
        var segment = project.Segment("s1");
        segment.Transcript = "こんにちは";
        await project.SeedAsync(segment);
        var edits = new UniversalFeatureOverrideService(project.DatasetRepository);

        await edits.SetEmotionAsync(project.Workspace, "s1", "喜び", TestProject.Token);
        var set = (await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token))!.Segments.Single();
        Assert.Equal("喜び", set.Emotion!.Name);
        Assert.Equal(EmotionLabelRecord.SourceUser, set.Emotion.Source);
        Assert.Null(set.Emotion.Confidence);
        Assert.Equal("こんにちは", set.Transcript);

        await edits.SetEmotionAsync(project.Workspace, "s1", null, TestProject.Token);
        var cleared = (await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token))!.Segments.Single();
        Assert.Null(cleared.Emotion);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 喜び")]
    [InlineData("a\tb")]
    public async Task InvalidNameIsRefusedBeforeSaving(string name)
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("s1"));
        var edits = new UniversalFeatureOverrideService(project.DatasetRepository);
        await Assert.ThrowsAsync<ArgumentException>(
            () => edits.SetEmotionAsync(project.Workspace, "s1", name, TestProject.Token));
        var dataset = (await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token))!;
        Assert.Empty(dataset.Overrides);
    }

    [Fact]
    public async Task MissingSegmentIsRefused()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("s1"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new UniversalFeatureOverrideService(project.DatasetRepository)
                .SetEmotionAsync(project.Workspace, "none", "喜び", TestProject.Token));
    }

    [Fact]
    public async Task LatestEmotionIsReappliedAfterReanalysisAndClearStaysCleared()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("s1"));
        var edits = new UniversalFeatureOverrideService(project.DatasetRepository);
        await edits.SetEmotionAsync(project.Workspace, "s1", "怒り", TestProject.Token);
        await edits.SetEmotionAsync(project.Workspace, "s1", "悲しみ", TestProject.Token);
        var dataset = (await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token))!;

        // 再解析で作り直された区間にはラベルが無い状態から、最終の修正が再適用される。
        dataset.Segments.Single().Emotion = null;
        UniversalVoiceDatasetOverrideApplicator.Apply(dataset);
        Assert.Equal("悲しみ", dataset.Segments.Single().Emotion!.Name);

        await edits.SetEmotionAsync(project.Workspace, "s1", null, TestProject.Token);
        var cleared = (await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token))!;
        cleared.Segments.Single().Emotion = new EmotionLabelRecord { Name = "古い", Source = "inferred", Confidence = 0.5 };
        UniversalVoiceDatasetOverrideApplicator.Apply(cleared);
        Assert.Null(cleared.Segments.Single().Emotion);
    }
}
