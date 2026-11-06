using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// 用途ごとの固定backendとrevision。正本: docs/design/v1-training/training-modes-and-backends.md「この設計の前提」。
public static class TrainingBackendCatalog
{
    public const string TalkBackend = "style-bert-vits2";
    public const string TalkRevision = "d8148f3090ee5038ca7b4e4b327116c64467f952";
    public const string SingingBackend = "diffsinger";
    public const string SingingRevision = "323a5693245b827caba0ab1a7371fe5ca885b061";

    public static (string Backend, string Revision) For(TrainingTarget target) => target switch
    {
        TrainingTarget.Talk => (TalkBackend, TalkRevision),
        TrainingTarget.Singing => (SingingBackend, SingingRevision),
        _ => throw new ArgumentOutOfRangeException(nameof(target), "ジョブの用途はトークか歌唱のどちらかです。")
    };
}
