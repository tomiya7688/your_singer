using System.Globalization;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Speaker;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

public sealed class EditingRegressionTests
{
    private static CancellationToken Token => TestProject.Token;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualExclusionAlwaysReachesTraining(bool correction)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("keep"), p.Segment("exclude"));
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "exclude", true, Token);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = correction }], Token);
        Assert.Equal(new[] { "keep" }, Assert.Single(batch.Jobs).SegmentIds);
        // 修正は投影で適用し、保存済みの観測結果は消さない。
        Assert.Contains((await p.DatasetRepository.LoadAsync(p.Workspace, Token))!.Segments, x => x.SegmentId == "exclude");
    }

    [Fact]
    public async Task RestoringExcludedSegmentRestoresTrainingInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b"));
        var edits = new SpeakerOverrideService(p.Speakers);
        await edits.ExcludeSegmentAsync(p.Workspace, "b", true, Token);
        await edits.ExcludeSegmentAsync(p.Workspace, "b", false, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.Equal(new[] { "a", "b" }, view.Segments.Select(x => x.SegmentId).Order(StringComparer.Ordinal));
    }

    // {
    //   責務: [MergeRecalculatesDurationAndPreservesCrossFileAssignments: 話者統合後に区間時間とファイル跨ぎ割当を保つ]
    //   処理: [1: 別ファイル・別話者の区間を用意する, 2: 話者を統合する, 3: 合計時間と統合後の割当を確認する]
    //   エラー: [話者統合または保存済み分析の検証に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task MergeRecalculatesDurationAndPreservesCrossFileAssignments()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("long", duration: 10),
            p.Segment("short", "spk_b", duration: 2, source: "src_b"));
        var edits = new SpeakerOverrideService(p.Speakers);
        await edits.MergeSpeakersAsync(p.Workspace, "spk_a", ["spk_b"], Token);
        var merged = (await p.Speakers.LoadAsync(p.Workspace, Token))!;
        Assert.Equal(12, Assert.Single(merged.Speakers).TotalDurationSec);
        Assert.All(merged.Assignments, x => Assert.Equal("spk_a", x.SpeakerId));
    }

    // {
    //   責務: [MergedSpeakerExclusionBuildsOnlyRemainingTalkInput: 統合話者から除外区間を外してトーク入力を作る]
    //   処理: [1: 別ファイルの話者を統合し区間を一つ除外する, 2: 使用可能時間とジョブを確認する, 3: 残った区間だけをトーク入力へ構築する]
    //   エラー: [除外の反映または学習入力の構築に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task MergedSpeakerExclusionBuildsOnlyRemainingTalkInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("long", duration: 10),
            p.Segment("short", "spk_b", duration: 2, source: "src_b"));
        var edits = new SpeakerOverrideService(p.Speakers);
        await edits.MergeSpeakersAsync(p.Workspace, "spk_a", ["spk_b"], Token);
        await edits.ExcludeSegmentAsync(p.Workspace, "long", true, Token);
        var speaker = Assert.Single((await p.Speakers.LoadAsync(p.Workspace, Token))!.Speakers);
        Assert.Equal(2, speaker.UsableDurationSec);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token);
        var job = Assert.Single(batch.Jobs);
        Assert.Equal(new[] { "short" }, job.SegmentIds);

        // 統合・除外済みの投影を使い、除外された音声が学習入力に入らないことを確認する。
        var input = await new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, job, "統合話者", Token);
        Assert.Equal("short", Assert.Single(input.Items).SegmentId);
        Assert.True(File.Exists(input.Items[0].AudioPath));
    }

    [Fact]
    public async Task InvalidMergeDoesNotOverwriteSavedAnalysis()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b", "spk_b"));
        var path = Path.Combine(p.Workspace.MetadataPath, "speakers.json");
        var before = await File.ReadAllBytesAsync(path, Token);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new SpeakerOverrideService(p.Speakers)
            .MergeSpeakersAsync(p.Workspace, "spk_a", ["spk_b", "missing"], Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task ClassificationEditsReachTalkAndSingingBuilders()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("to_singing"), p.Segment("to_talk", type: SegmentContentType.Singing));
        var edits = new ContentClassificationOverrideService(p.Classifications);
        await edits.SetContentTypeAsync(p.Workspace, "to_singing", SegmentContentType.Singing, Token);
        await edits.SetContentTypeAsync(p.Workspace, "to_talk", SegmentContentType.Speech, Token);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Both, AutoCorrectionEnabled = false }], Token);
        var talk = Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Talk);
        var singing = Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Singing);
        Assert.Equal(new[] { "to_talk" }, talk.SegmentIds);
        Assert.Equal(new[] { "to_singing" }, singing.SegmentIds);
        var talkData = await new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, talk, "話者あ", Token);
        var singingData = await new DiffSingerDatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, singing, Token);
        Assert.Equal("to_talk", Assert.Single(talkData.Items).SegmentId);
        Assert.Equal("to_singing", Assert.Single(singingData.Items).SegmentId);
        Assert.True(File.Exists(talkData.Items[0].AudioPath));
    }

    [Fact]
    public async Task FingerprintChangesWhenTranscriptOrFeaturesChange()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var first = await p.Correction().BuildAsync(p.Workspace, false, Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "修正した文章";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        var second = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        dataset.Segments[0].MeanF0Hz = 440;
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        var third = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.NotEqual(second.Fingerprint, third.Fingerprint);
    }

    [Fact]
    public async Task FingerprintIsIndependentOfCultureAndSegmentOrder()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a", confidence: 0.5), p.Segment("b"));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            var first = await p.Correction().BuildAsync(p.Workspace, true, Token);
            var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
            dataset.Segments.Reverse();
            await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = await p.Correction().BuildAsync(p.Workspace, true, Token);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(TrainingTarget.Talk, 1)]
    [InlineData(TrainingTarget.Singing, 1)]
    [InlineData(TrainingTarget.Both, 2)]
    public async Task TargetSelectionCreatesSeparateJobs(TrainingTarget target, int expected)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("talk"), p.Segment("song", type: SegmentContentType.Singing),
            p.Segment("uncertain", type: SegmentContentType.Ambiguous));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = target, AutoCorrectionEnabled = false }], Token);
        Assert.Equal(expected, batch.Jobs.Count);
        Assert.All(batch.Jobs, x => Assert.DoesNotContain("uncertain", x.SegmentIds));
        var restored = await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token);
        Assert.NotNull(restored);
        Assert.Equal(batch.Jobs.Select(x => x.DatasetFingerprint), restored.Jobs.Select(x => x.DatasetFingerprint));
        var retry = await p.Planner().RecreateBatchAsync(p.Workspace, batch, Token);
        Assert.NotEqual(batch.BatchId, retry.BatchId);
        Assert.Equal(batch.Jobs.Select(x => x.Target), retry.Jobs.Select(x => x.Target));
        Assert.All(retry.Jobs, x => Assert.False(x.AutoCorrectionEnabled));
    }

    [Fact]
    public async Task AutomaticRejectionIsDisabledButManualEditsRemainWhenOff()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("weak", confidence: 0.1), p.Segment("strong"));
        var on = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var off = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.DoesNotContain(on.Segments, x => x.SegmentId == "weak");
        Assert.Contains(off.Segments, x => x.SegmentId == "weak");
        Assert.NotEqual(on.Fingerprint, off.Fingerprint);
    }

    // {
    //   責務: [CorrectionToggleChangesBuiltTalkInput: 補正設定による区間選択がトーク学習入力へ反映されることを確認する]
    //   処理: [1: 高信頼・低信頼区間の合成データを用意する, 2: 補正ON/OFF別のジョブを作る, 3: 各ジョブから異なる学習入力を作る]
    //   エラー: [ジョブまたはトーク入力の構築に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task CorrectionToggleChangesBuiltTalkInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("weak", confidence: 0.1), p.Segment("strong", confidence: 0.95));
        var planner = p.Planner();
        var enabledBatch = await planner.CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = true }], Token);
        var disabledBatch = await planner.CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }], Token);
        var enabledJob = Assert.Single(enabledBatch.Jobs);
        var disabledJob = Assert.Single(disabledBatch.Jobs);
        Assert.Equal(new[] { "strong" }, enabledJob.SegmentIds);
        Assert.Equal(new[] { "strong", "weak" }, disabledJob.SegmentIds);

        // ONでは自動除外され、OFFでは残る区間が生成済み学習入力にもそのまま反映される。
        var builder = new StyleBertVits2DatasetBuilder(p.DatasetRepository);
        var enabledInput = await builder.BuildAsync(p.Workspace, enabledJob, "話者", Token);
        var disabledInput = await builder.BuildAsync(p.Workspace, disabledJob, "話者", Token);
        Assert.True(enabledInput.AutoCorrectionEnabled);
        Assert.False(disabledInput.AutoCorrectionEnabled);
        Assert.Equal(new[] { "strong" }, enabledInput.Items.Select(item => item.SegmentId));
        Assert.Equal(new[] { "strong", "weak" },
            disabledInput.Items.Select(item => item.SegmentId).Order(StringComparer.Ordinal));
        Assert.All(enabledInput.Items.Concat(disabledInput.Items), item => Assert.True(File.Exists(item.AudioPath)));
    }

    // {
    //   責務: [DifferentFileSpeakersProduceSeparateTalkInputs: 別ファイルの話者別素材が別々の学習入力になることを確認する]
    //   処理: [1: 話者別・ファイル別の合成区間を用意する, 2: 話者別ジョブとトーク学習入力を構築する, 3: 入力が混在しないことを確認する]
    //   エラー: [ジョブ計画または学習入力の構築に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task DifferentFileSpeakersProduceSeparateTalkInputs()
    {
        using var p = new TestProject();
        await p.SeedAsync(
            p.Segment("speaker_a_segment", "spk_a", source: "source_a"),
            p.Segment("speaker_b_segment", "spk_b", source: "source_b"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk },
             new() { SpeakerId = "spk_b", Target = TrainingTarget.Talk }], Token);
        var speakerAJob = Assert.Single(batch.Jobs, x => x.SpeakerId == "spk_a");
        var speakerBJob = Assert.Single(batch.Jobs, x => x.SpeakerId == "spk_b");
        Assert.Equal(new[] { "speaker_a_segment" }, speakerAJob.SegmentIds);
        Assert.Equal(new[] { "speaker_b_segment" }, speakerBJob.SegmentIds);
        Assert.NotEqual(speakerAJob.DatasetFingerprint, speakerBJob.DatasetFingerprint);

        // 各ジョブを既存のトーク入力構築へ渡し、別話者の音声が混ざらないことを確認する。
        var builder = new StyleBertVits2DatasetBuilder(p.DatasetRepository);
        var speakerAInput = await builder.BuildAsync(p.Workspace, speakerAJob, "話者A", Token);
        var speakerBInput = await builder.BuildAsync(p.Workspace, speakerBJob, "話者B", Token);
        Assert.Equal("speaker_a_segment", Assert.Single(speakerAInput.Items).SegmentId);
        Assert.Equal("speaker_b_segment", Assert.Single(speakerBInput.Items).SegmentId);
        Assert.NotEqual(speakerAInput.Items[0].AudioPath, speakerBInput.Items[0].AudioPath);
        Assert.True(File.Exists(speakerAInput.Items[0].AudioPath));
        Assert.True(File.Exists(speakerBInput.Items[0].AudioPath));
    }

    // {
    //   責務: [SameSpeakerAcrossFilesProducesCombinedTalkInput: 同じ話者の複数ファイル素材を一つのトーク入力へまとめる]
    //   処理: [1: 同じ話者に属する別ファイル由来の合成区間を用意する, 2: 話者別ジョブとトーク入力を構築する, 3: 両方の区間が一つの入力に含まれることを確認する]
    //   エラー: [ジョブ計画または学習入力の構築に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task SameSpeakerAcrossFilesProducesCombinedTalkInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(
            p.Segment("first_file_segment", "spk_a", source: "source_a"),
            p.Segment("second_file_segment", "spk_a", source: "source_b"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token);
        var job = Assert.Single(batch.Jobs);
        Assert.Equal(
            new[] { "first_file_segment", "second_file_segment" },
            job.SegmentIds.Order(StringComparer.Ordinal));

        // 別ファイルに分かれていた区間が同じ話者の学習入力へ集約されることを確認する。
        var input = await new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, job, "同じ話者", Token);
        Assert.Equal(
            new[] { "first_file_segment", "second_file_segment" },
            input.Items.Select(item => item.SegmentId).Order(StringComparer.Ordinal));
        Assert.Equal(2, input.Items.Select(item => item.AudioPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(input.Items, item => Assert.True(File.Exists(item.AudioPath)));
    }
}
