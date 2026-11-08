using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [MaterialAmountPresenterTests: 素材量の日本語表示(秒の整形・教師なし・未測定を0にしない)を確認する] }
public sealed class MaterialAmountPresenterTests
{
    private static SpeakerDataInventory Make(double speech, double singing, double identity, Dictionary<string, string> status) => new()
    {
        SchemaVersion = "1",
        SpeakerId = "spk",
        DatasetFingerprint = "f",
        CalculationVersion = "1",
        InventoryRevision = "r",
        Speech = new SpeechInventory
        {
            RawSeconds = speech + 30,
            EligibleSeconds = speech,
            DistinctRecordings = 2,
            IndependentUtterances = 7,
            TranscriptUsableCount = 5,
            PhonemeCoverageCount = 20
        },
        Singing = new SingingInventory
        {
            RawSeconds = singing,
            EligibleSeconds = singing,
            DistinctRecordings = 1,
            IndependentPhrases = 3,
            PitchBinsObserved = 2
        },
        IdentityReference = new IdentityReferenceInventory { EligibleSeconds = identity, DistinctSources = 4 },
        MeasurementStatus = status,
        DerivedTags = []
    };

    private static Dictionary<string, string> AllMeasured() => new()
    {
        ["duration"] = "measured",
        ["transcription"] = "measured",
        ["singing_f0"] = "measured",
        ["speaker_identity"] = "measured"
    };

    [Fact]
    public void ThreeAxesAreShownSeparatelyWithMinutesAndSeconds()
    {
        var text = string.Join("\n", MaterialAmountPresenter.Describe(Make(125, 0, 40, AllMeasured())));
        Assert.Contains("発話教師: 2分05秒(割り当て済みの全体 2分35秒) / 独立した発話 7件", text);
        // 歌唱教師が無いことは「なし」で、発話や声質参照で代用したとは書かない。
        Assert.Contains("歌唱教師: なし", text);
        Assert.Contains("声質参照: 40秒 / 元の録音 4件", text);
        Assert.DoesNotContain("未測定", text);
    }

    [Fact]
    public void UnmeasuredAxesAreNotShownAsZeroOrAsMeasured()
    {
        var status = AllMeasured();
        status["singing_f0"] = "partially_measured";
        status.Remove("transcription");
        status["duration"] = "unavailable";
        var text = string.Join("\n", MaterialAmountPresenter.Describe(Make(60, 300, 0, status)));
        Assert.Contains("書き起こしの確信度は未測定", text);
        Assert.Contains("音高は一部未測定", text);
        Assert.Contains("注意: 区間の長さは未測定", text);
        Assert.Contains("0秒として扱わず", text);
    }
}
