using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Tests;

// 学習用Assessmentの保存・読込・旧UVD取り込みの境界を確認する。判定アルゴリズムは対象外。
public sealed class TrainingAssessmentStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly TrainingAssessmentRepository _repository = new();

    private static TrainingMeasurement M(double? value, string status) => new() { Value = value, Status = status };

    private static TrainingAssessmentItem Item(string id, string eligibility = "eligible", double weight = 1.0,
        double? quality = 0.72, string qualityStatus = "measured", List<string>? reasons = null) => new()
    {
        SegmentId = id, Eligibility = eligibility, ReasonCodes = reasons ?? [], RawWeight = 1.0, TrainingWeight = weight,
        Quality = new()
        {
            Value = quality, Status = qualityStatus,
            Components = new() { ["audio"] = M(0.0, "measured"), ["alignment"] = M(null, "uncalibrated") }
        },
        Utility = new() { Value = 0.75, Status = "measured", ReasonCodes = ["rare_phoneme"] }
    };

    private static TrainingAssessmentRecord Record(string jobId = "job-1", params TrainingAssessmentItem[] items) => new()
    {
        SchemaVersion = "1", PolicyVersion = "training-weight-v1", JobId = jobId, SpeakerId = "spk_02",
        Target = "talk", SourceDatasetFingerprint = "sha256-abc", Items = [.. items]
    };

    private static string AssessmentPath(TestProject p, string jobId = "job-1") =>
        Path.Combine(p.Workspace.TrainingPath, "assessments", jobId, "assessment.json");

    [Fact]
    public async Task RoundTrip_PreservesNullZeroOrderAndReasons()
    {
        using var p = new TestProject();
        var item1 = Item("seg_1");
        var item2 = Item("seg_2", "excluded", 0, quality: null, qualityStatus: "unavailable", reasons: ["wrong_speaker", "user_excluded"]);
        await _repository.SaveAsync(p.Workspace, Record("job-1", item1, item2), Token);

        var loaded = await _repository.LoadAsync(p.Workspace, "job-1", Token);

        Assert.NotNull(loaded);
        Assert.Equal(["seg_1", "seg_2"], loaded.Items.Select(x => x.SegmentId));
        Assert.Equal(0.0, loaded.Items[0].Quality.Components["audio"].Value);
        Assert.Null(loaded.Items[0].Quality.Components["alignment"].Value);
        Assert.Null(loaded.Items[1].Quality.Value);
        Assert.Equal(["wrong_speaker", "user_excluded"], loaded.Items[1].ReasonCodes);
        Assert.Equal(["rare_phoneme"], loaded.Items[0].Utility.ReasonCodes);
        var json = await File.ReadAllTextAsync(AssessmentPath(p), Token);
        Assert.Contains("\"schema_version\": \"1\"", json);
        Assert.Contains("\"training_weight\"", json);
        Assert.Contains("\"value\": 0", json);
        Assert.Contains("\"value\": null", json);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        using var p = new TestProject();
        Assert.Null(await _repository.LoadAsync(p.Workspace, "none", Token));
    }

    [Fact]
    public async Task Load_LegacyUvd_YieldsLegacyUnknown()
    {
        using var p = new TestProject();
        var path = Path.Combine(p.Workspace.MetadataPath, "universal-voice-dataset.json");
        // 旧形式: 状態フィールドが無く、0が測定済みか未計測か判別できない。
        await File.WriteAllTextAsync(path, """
            {"schema_version":"1","stage_version":"old","segments":[
              {"segment_id":"seg_b","source_id":"s","audio_path":"a.wav","cache_key":"k","asr_confidence":0.0,"alignment_confidence":0.9},
              {"segment_id":"seg_a","source_id":"s","audio_path":"a.wav","cache_key":"k","asr_confidence":0.8,"asr_confidence_status":"measured",
               "alignment_confidence":0.0,"alignment_confidence_status":"uncalibrated"}]}
            """, Token);
        var before = await File.ReadAllBytesAsync(path, Token);

        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        var imported = TrainingAssessmentLegacyImporter.ImportComponents(dataset);

        Assert.Equal(["seg_a", "seg_b"], imported.Keys);
        Assert.Equal(((double?)0.0, "legacy_unknown"), Normalize(imported["seg_b"]["asr"]));
        Assert.Equal(((double?)0.9, "legacy_unknown"), Normalize(imported["seg_b"]["alignment"]));
        Assert.Equal(((double?)0.8, "measured"), Normalize(imported["seg_a"]["asr"]));
        Assert.Equal(((double?)0.0, "uncalibrated"), Normalize(imported["seg_a"]["alignment"]));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }

    [Theory]
    [InlineData(double.NaN, "measured", null, "invalid")]
    [InlineData(1.5, "measured", null, "invalid")]
    [InlineData(double.PositiveInfinity, "legacy_unknown", null, "legacy_unknown")]
    [InlineData(0.5, "unavailable", null, "unavailable")]
    public void LegacyConversion_DoesNotKeepBadValues(double value, string status, double? expected, string expectedStatus)
    {
        var result = TrainingAssessmentLegacyImporter.FromLegacyConfidence(value, status);
        Assert.Equal(expected, result.Value);
        Assert.Equal(expectedStatus, result.Status);
    }

    private static (double?, string) Normalize(TrainingMeasurement m) => (m.Value, m.Status);

    public static TheoryData<string, string> InvalidDocuments() => new()
    {
        { "enum", "\"eligibility\": \"eligible\"|\"eligibility\": \"maybe\"" },
        { "status", "\"status\": \"measured\"|\"status\": \"calibrated\"" },
        { "reason", "\"reason_codes\": []|\"reason_codes\": [\"low_quality\"]" },
        { "nan", "\"raw_weight\": 1|\"raw_weight\": NaN" },
        { "infinity", "\"raw_weight\": 1|\"raw_weight\": Infinity" },
        { "nan_string", "\"raw_weight\": 1|\"raw_weight\": \"NaN\"" },
        { "range", "\"value\": 0.72|\"value\": 1.2" },
        { "measured_null", "\"value\": 0.72|\"value\": null" },
        { "schema", "\"schema_version\": \"1\"|\"schema_version\": \"2\"" },
        { "unknown_field", "\"raw_weight\": 1|\"raw_weight\": 1, \"extra\": 1" },
        { "eligible_zero_weight", "\"training_weight\": 1|\"training_weight\": 0" },
        { "escaping_job", "\"job_id\": \"job-1\"|\"job_id\": \"../job-1\"" },
        { "dup", "\"segment_id\": \"seg_2\"|\"segment_id\": \"seg_1\"" },
        { "order", "\"segment_id\": \"seg_1\"|\"segment_id\": \"seg_9\"" }
    };

    private static string ValidJson() => """
        {"schema_version": "1","policy_version": "training-weight-v1","job_id": "job-1","speaker_id": "spk_02","target": "talk",
         "source_dataset_fingerprint": "sha256-abc","items": [
          {"segment_id": "seg_1","eligibility": "eligible","reason_codes": [],
           "quality": {"value": 0.72,"status": "measured","components": {}},
           "utility": {"value": 0.75,"status": "measured","reason_codes": []},
           "raw_weight": 1,"training_weight": 1},
          {"segment_id": "seg_2","eligibility": "eligible","reason_codes": [],
           "quality": {"value": null,"status": "unavailable","components": {}},
           "utility": {"value": null,"status": "unavailable","reason_codes": []},
           "raw_weight": 1,"training_weight": 1}]}
        """;

    [Fact]
    public async Task Load_ValidFixture_Succeeds()
    {
        using var p = new TestProject();
        Directory.CreateDirectory(Path.GetDirectoryName(AssessmentPath(p))!);
        await File.WriteAllTextAsync(AssessmentPath(p), ValidJson(), Token);
        Assert.Equal(2, (await _repository.LoadAsync(p.Workspace, "job-1", Token))!.Items.Count);
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task Load_InvalidEnumNanInfinityDuplicateSegmentOrEscapingPath_FailsInJapanese(string name, string edit)
    {
        using var p = new TestProject();
        var parts = edit.Split('|');
        var json = ValidJson();
        Assert.Contains(parts[0], json);
        // 最初の一致だけ置換する(重複・順序はseg_2側を書き換える)。
        var target = name == "dup" ? json.LastIndexOf(parts[0], StringComparison.Ordinal) : json.IndexOf(parts[0], StringComparison.Ordinal);
        json = json.Remove(target, parts[0].Length).Insert(target, parts[1]);
        var path = AssessmentPath(p);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, Token);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _repository.LoadAsync(p.Workspace, "job-1", Token));
        Assert.Contains("学習用Assessmentが不正です", ex.Message);
        Assert.Matches("[ぁ-んァ-ヶ]", ex.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("C:evil")]
    public async Task PathOutsideWorkspace_IsRejectedOnLoadAndSave(string jobId)
    {
        using var p = new TestProject();
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.LoadAsync(p.Workspace, jobId, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.SaveAsync(p.Workspace, Record(jobId, Item("seg_1")), Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.TrainingPath, "assessments")));
    }

    [Fact]
    public async Task Save_InvalidRecord_FailsInJapaneseAndWritesNothing()
    {
        using var p = new TestProject();
        var duplicate = Record("job-1", Item("seg_1"), Item("seg_1"));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _repository.SaveAsync(p.Workspace, duplicate, Token));
        Assert.Contains("重複", ex.Message);
        var nan = Record("job-1", Item("seg_1", quality: double.NaN));
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.SaveAsync(p.Workspace, nan, Token));
        var unsorted = Record("job-1", Item("seg_2"), Item("seg_1"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.SaveAsync(p.Workspace, unsorted, Token));
        Assert.False(File.Exists(AssessmentPath(p)));
    }

    [Fact]
    public async Task Save_CanceledOrInterrupted_DoesNotPublishPartial()
    {
        using var p = new TestProject();
        await _repository.SaveAsync(p.Workspace, Record("job-1", Item("seg_1")), Token);
        var published = await File.ReadAllBytesAsync(AssessmentPath(p), Token);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.SaveAsync(p.Workspace, Record("job-1", Item("seg_1"), Item("seg_2")), canceled.Token));

        Assert.Equal(published, await File.ReadAllBytesAsync(AssessmentPath(p), Token));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(AssessmentPath(p))!, "*.tmp"));
    }

    [Fact]
    public async Task Save_WithLeftoverPartialTemp_StillLoadsPublishedVersion()
    {
        using var p = new TestProject();
        await _repository.SaveAsync(p.Workspace, Record("job-1", Item("seg_1")), Token);
        // 前回プロセスが途中で落ちて残した書きかけの一時ファイル。公開済みの読込に影響しない。
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(AssessmentPath(p))!, ".assessment-dead.tmp"), "{\"schema_ver", Token);

        var loaded = await _repository.LoadAsync(p.Workspace, "job-1", Token);
        Assert.Single(loaded!.Items);

        await _repository.SaveAsync(p.Workspace, Record("job-1", Item("seg_1"), Item("seg_2")), Token);
        Assert.Equal(2, (await _repository.LoadAsync(p.Workspace, "job-1", Token))!.Items.Count);
    }
}
