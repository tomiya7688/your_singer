using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [HardwareProbeTests: nvidia-smi出力の解釈と、取得できない場合にCUDAなしへ倒れることを確認する] }
public sealed class HardwareProbeTests
{
    [Theory]
    [InlineData("12288\n", true, 12288)]
    [InlineData("8192\r\n24576\r\n", true, 24576)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    [InlineData("NVIDIA-SMI has failed\n", false, null)]
    [InlineData("-5\n0\n", false, null)]
    public void ParseReadsTheLargestGpuAndFallsBackToNoCuda(string? output, bool cuda, int? vram)
    {
        var info = HardwareProbe.Parse(output);
        Assert.Equal((cuda, vram), (info.HasCuda, info.VramMb));
    }

    [Fact]
    public async Task DetectNeverThrowsEvenWithoutNvidiaSmi()
    {
        var info = await HardwareProbe.DetectAsync(TestProject.Token);
        Assert.Equal(info.HasCuda, info.VramMb is not null);
    }
}
