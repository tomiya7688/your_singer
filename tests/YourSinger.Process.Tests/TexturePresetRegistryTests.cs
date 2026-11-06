using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 質感プリセットの宣言・検証・登録簿を確認する。実音声の差の確認は #99/#100/#108 の範囲。
public sealed class TexturePresetRegistryTests
{
    private static string Json(
        string id = "acme.warm",
        string version = "1.0.0",
        string traits = """{"breathiness":0.6,"tension":0.2}""",
        string targets = """["talk","singing"]""",
        string schema = "1",
        string description = "あたたかい質感",
        string extra = "") => $$"""
        {
          "schema_version": "{{schema}}", "preset_id": "{{id}}", "version": "{{version}}",
          "display_name_ja": "あたたかい", "description_ja": "{{description}}",
          "supported_targets": {{targets}}, "traits": {{traits}},
          "required_capability_tags": ["breath_control"], "preferred_strategy_tags": []{{extra}}
        }
        """;

    [Fact]
    public void BuiltIns_AreLoadedAndNotClaimedAsAvailable()
    {
        var registry = new TexturePresetRegistry();
        Assert.Equal(8, registry.Entries.Count);
        Assert.Contains(registry.Entries, x => x.Preset.PresetId == "builtin.natural");
        Assert.All(registry.Entries, x =>
        {
            Assert.Equal(TexturePresetSource.BuiltIn, x.Source);
            Assert.Equal(TexturePresetStatus.Unsupported, x.Status);
            Assert.False(string.IsNullOrWhiteSpace(x.Reason));
            Assert.All(x.Preset.Traits.Values, v => Assert.InRange(v, 0, 1));
        });
    }

    [Fact]
    public void ModJson_CanBeAddedWithoutRebuild_AndRealizationIsDecidedByTheCaller()
    {
        var registry = new TexturePresetRegistry(p => p.RequiredCapabilityTags.Contains("breath_control")
            ? (TexturePresetStatus.Partial, "一部の工程だけ対応") : (TexturePresetStatus.Unsupported, "未対応"));
        var result = registry.RegisterMod(Json(), "mods/warm/preset.json");
        Assert.True(result.Accepted);
        Assert.Equal(TexturePresetStatus.Partial, result.Entry!.Status);
        Assert.Equal(TexturePresetSource.Mod, result.Entry.Source);
        Assert.Equal(9, registry.Entries.Count);
    }

    [Fact]
    public void IdConflict_SameKeyDifferentContent_Rejected_SameContentIsIdempotent()
    {
        var registry = new TexturePresetRegistry();
        Assert.True(registry.RegisterMod(Json(), "a").Accepted);
        Assert.True(registry.RegisterMod(Json(), "a2").Accepted);
        Assert.Equal(9, registry.Entries.Count);
        var conflict = registry.RegisterMod(Json(description: "別の説明"), "b");
        Assert.False(conflict.Accepted);
        Assert.Contains("内容が異なります", conflict.Failure);
        Assert.Equal(9, registry.Entries.Count);
    }

    [Fact]
    public void BuiltInId_CannotBeHijacked()
    {
        var registry = new TexturePresetRegistry();
        Assert.False(registry.RegisterMod(Json(id: "builtin.natural", version: "9.0.0"), "evil").Accepted);
        Assert.False(registry.RegisterMod(Json(id: "builtin.newone"), "evil2").Accepted);
        Assert.Equal(8, registry.Entries.Count);
        Assert.Equal("1.0.0", registry.Find("builtin.natural")!.Preset.Version);
    }

    [Fact]
    public void Versions_AreKeptAndOrderedBySemVer()
    {
        var registry = new TexturePresetRegistry();
        foreach (var version in new[] { "1.10.0", "1.2.0", "1.10.0-beta", "2.0.0" })
            Assert.True(registry.RegisterMod(Json(version: version), version).Accepted);
        var versions = registry.Entries.Where(x => x.Preset.PresetId == "acme.warm").Select(x => x.Preset.Version).ToArray();
        Assert.Equal(["1.2.0", "1.10.0-beta", "1.10.0", "2.0.0"], versions);
        Assert.Equal("2.0.0", registry.Find("acme.warm")!.Preset.Version);
        Assert.Equal("1.2.0", registry.Find("acme.warm", "1.2.0")!.Preset.Version);
    }

    [Theory]
    [InlineData("""{"tension":1.2}""")]
    [InlineData("""{"tension":-0.1}""")]
    [InlineData("""{"unknown_trait":0.5}""")]
    [InlineData("""{}""")]
    public void InvalidTraits_AreRejected(string traits)
    {
        var registry = new TexturePresetRegistry();
        var result = registry.RegisterMod(Json(traits: traits), "bad");
        Assert.False(result.Accepted);
        Assert.Equal(8, registry.Entries.Count);
        Assert.Single(registry.Failures);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void BrokenJson_RejectsOnlyThatMod(string broken)
    {
        var registry = new TexturePresetRegistry();
        Assert.True(registry.RegisterMod(Json(), "good").Accepted);
        Assert.False(registry.RegisterMod(broken, "broken").Accepted);
        Assert.Equal(9, registry.Entries.Count);
        Assert.Equal("broken", Assert.Single(registry.Failures).Origin);
    }

    [Fact]
    public void UnsupportedSchemaUnknownFieldAndBadId_AreRejectedWithReason()
    {
        var registry = new TexturePresetRegistry();
        Assert.Contains("schema_version", registry.RegisterMod(Json(schema: "2"), "a").Failure);
        Assert.Contains("未知の項目", registry.RegisterMod(Json(extra: ""","extra":1"""), "b").Failure);
        Assert.Contains("preset_id", registry.RegisterMod(Json(id: "NoDot"), "c").Failure);
        Assert.Contains("SemVer", registry.RegisterMod(Json(version: "1.0"), "d").Failure);
        Assert.Contains("supported_targets", registry.RegisterMod(Json(targets: """["video"]"""), "e").Failure);
    }

    [Fact]
    public void RegisterModFile_ReportsMissingFileAsFailure()
    {
        var registry = new TexturePresetRegistry();
        var result = registry.RegisterModFile(Path.Combine(Path.GetTempPath(), "missing-preset-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.False(result.Accepted);
        Assert.Single(registry.Failures);
    }

    [Fact]
    public void Select_IsIndependentOfOutputTarget_ButRejectsUnsupportedTarget()
    {
        var registry = new TexturePresetRegistry();
        registry.RegisterMod(Json(targets: """["singing"]"""), "singing-only");
        var (request, entry) = registry.Select("builtin.natural", null, "talk", ["seg1"]);
        Assert.Equal("builtin.natural", request.Preset);
        Assert.Equal("1.0.0", request.PresetVersion);
        Assert.Equal(["seg1"], request.ReferenceSegmentIds);
        Assert.Equal(TexturePresetStatus.Unsupported, entry.Status);
        Assert.Throws<ArgumentException>(() => registry.Select("acme.warm", null, "talk"));
        Assert.Throws<ArgumentException>(() => registry.Select("acme.unknown", null, "talk"));
        Assert.Equal("singing", registry.Select("acme.warm", null, "singing").Entry.Preset.SupportedTargets.Single());
    }

    [Fact]
    public void JobFingerprint_ReflectsPresetVersionAndReferences_ButNotWhenAbsent()
    {
        TrainingJobRecord Job(string? version, params string[] refs) => new()
        {
            JobId = "j", SpeakerId = "s", Target = TrainingTarget.Talk, DatasetFingerprint = "fp",
            RequestedTexture = new TrainingTextureRequest { Preset = "builtin.natural", PresetVersion = version, ReferenceSegmentIds = [.. refs] },
            RequestedTrainingMode = TrainingMode.Auto
        };
        var unversioned = TrainingJobContractFingerprint.Compute(Job(null));
        var v1 = TrainingJobContractFingerprint.Compute(Job("1.0.0"));
        Assert.NotEqual(unversioned, v1);
        Assert.NotEqual(v1, TrainingJobContractFingerprint.Compute(Job("1.1.0")));
        Assert.NotEqual(v1, TrainingJobContractFingerprint.Compute(Job("1.0.0", "seg1")));
        Assert.Equal(unversioned, TrainingJobContractFingerprint.Compute(Job(null)));
    }

    [Fact]
    public void BuiltInHash_IsStable_AndChangesWithContent()
    {
        var first = BuiltInTexturePresets.All[0];
        Assert.Equal(TexturePresetValidator.Hash(first), TexturePresetValidator.Hash(first));
        var changed = TexturePresetValidator.Parse(Json());
        Assert.NotEqual(TexturePresetValidator.Hash(first), TexturePresetValidator.Hash(changed));
        Assert.Equal(1, TexturePresetValidator.CompareVersions("1.0.0", "1.0.0-rc"));
    }
}
