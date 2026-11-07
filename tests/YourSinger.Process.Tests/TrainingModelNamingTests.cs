using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [TrainingModelNamingTests: モデル名が安全な文字だけで、別jobと衝突せず、長さが制限されることを確認する] }
public sealed class TrainingModelNamingTests
{
    [Fact]
    public void NamesUseSafeCharactersAndDifferPerJob()
    {
        var a = TrainingModelNaming.ModelName("spk a/../b", "11111111-2222");
        var b = TrainingModelNaming.ModelName("spk a/../b", "99999999-2222");
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, b);
        Assert.True(TrainingModelNaming.ModelName(new string('x', 200), "j").Length <= 64);
        Assert.Throws<ArgumentException>(() => TrainingModelNaming.ModelName(" ", "j"));
    }
}
