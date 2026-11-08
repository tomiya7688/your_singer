using System.Text.Json;
using YourSinger.Data.Models;

namespace YourSinger.Process.Tests;

// { 責務: [EmotionLabelTests: 区間の感情ラベルの検証と、未設定のとき保存結果が変わらないこと・旧データが読めることを確認する] }
public sealed class EmotionLabelTests
{
    private static UniversalVoiceSegment Segment(EmotionLabelRecord? emotion = null) => new()
    {
        SegmentId = "s1",
        SourceId = "src",
        AudioPath = "a.wav",
        CacheKey = "k",
        Emotion = emotion
    };

    [Fact]
    public void UnsetEmotionIsNotWrittenSoOldAndNewFilesLookTheSame()
    {
        var json = JsonSerializer.Serialize(Segment());
        Assert.DoesNotContain("Emotion", json);
        var back = JsonSerializer.Deserialize<UniversalVoiceSegment>(json)!;
        Assert.Null(back.Emotion);
    }

    [Fact]
    public void LabelRoundTripsAndKeepsUnmeasuredConfidenceAsNull()
    {
        var label = new EmotionLabelRecord { Name = "喜び", Source = EmotionLabelRecord.SourceInferred, Confidence = null };
        var back = JsonSerializer.Deserialize<UniversalVoiceSegment>(JsonSerializer.Serialize(Segment(label)))!;
        Assert.Equal("喜び", back.Emotion!.Name);
        Assert.Null(back.Emotion.Confidence);
        Assert.Null(back.Emotion.Validate());
    }

    [Theory]
    [InlineData("", "user", null, "空")]
    [InlineData(" 喜び", "user", null, "空白")]
    [InlineData("a\tb", "user", null, "制御文字")]
    [InlineData("喜び", "ai", null, "user か inferred")]
    [InlineData("喜び", "inferred", 1.5, "0〜1")]
    [InlineData("喜び", "inferred", double.NaN, "0〜1")]
    [InlineData("喜び", "user", 0.9, "確信度は付けられません")]
    public void InvalidLabelsAreRefusedWithReason(string name, string source, double? confidence, string expected)
    {
        var reason = new EmotionLabelRecord { Name = name, Source = source, Confidence = confidence }.Validate();
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void TooLongNameIsRefused()
    {
        var reason = new EmotionLabelRecord { Name = new string('あ', 33), Source = "user" }.Validate();
        Assert.Contains("32文字以内", reason);
    }
}
