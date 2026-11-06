using System.Diagnostics;
using System.Globalization;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [HardwareProbe: nvidia-smi の出力から、学習前検証に渡すGPU情報(CUDA有無・最大VRAM)を作る]
//   処理: [1: nvidia-smi でGPUごとの総メモリ(MiB)を取得する 2: 最大値を VRAM とする 3: 取得できなければ CUDA なしとして扱う]
//   補足: [
//     これは搭載メモリの申告値であり、学習が実際に必要とするVRAMの測定(#88)ではない。
//     nvidia-smi が無い・失敗・出力不正のときは「CUDAなし(VRAM不明)」にして学習を始めない側に倒す。
//   ]
// }
public static class HardwareProbe
{
    // {
    //   責務: [Parse: nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits の出力を解釈する]
    //   引数: [output: 1行1GPUのMiB数]
    //   戻り値: [info: 有効なGPUが1つ以上あれば HasCuda=true と最大VRAM。無ければ HasCuda=false]
    // }
    public static HardwareInfo Parse(string? output)
    {
        var sizes = (output ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) && mib > 0 ? mib : (int?)null)
            .Where(mib => mib is not null).Select(mib => mib!.Value).ToArray();
        return sizes.Length == 0 ? new HardwareInfo(false, null) : new HardwareInfo(true, sizes.Max());
    }

    // {
    //   責務: [DetectAsync: nvidia-smi を実行してGPU情報を取得する]
    //   戻り値: [info: 取得できなければ CUDAなし]
    //   補足: [待機は10秒で打ち切る。取消は呼び出し側のトークンに従う]
    // }
    public static async Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return new HardwareInfo(false, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 ? Parse(output) : new HardwareInfo(false, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new HardwareInfo(false, null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nvidia-smi が無い環境(GPUなし)。
            return new HardwareInfo(false, null);
        }
    }
}
