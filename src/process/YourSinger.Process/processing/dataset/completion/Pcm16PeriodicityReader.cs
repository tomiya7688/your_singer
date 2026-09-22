using System.Security.Cryptography;
using System.Text;

namespace YourSinger.Process.Processing.Dataset.Completion;

/// <summary>元波形を変更せず、補間候補の周期と欠損フレーム自体の音量を調べる。</summary>
internal sealed class Pcm16PeriodicityReader : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly long _sampleCount;
    private readonly int _sampleRate;
    public string ContentHash { get; }

    private Pcm16PeriodicityReader(FileStream stream, long dataOffset, long sampleCount, int sampleRate, string hash)
    {
        _stream = stream; _dataOffset = dataOffset; _sampleCount = sampleCount; _sampleRate = sampleRate; ContentHash = hash;
    }

    public static async Task<Pcm16PeriodicityReader> OpenAsync(string path, CancellationToken token)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 44 || Four(reader) != "RIFF") throw Invalid();
            var limit = (long)reader.ReadUInt32() + 8;
            if (limit > stream.Length || Four(reader) != "WAVE") throw Invalid();
            var sampleRate = 0;
            var hasFormat = false;
            long dataOffset = -1;
            long sampleCount = 0;
            while (stream.Position + 8 <= limit)
            {
                token.ThrowIfCancellationRequested();
                var id = Four(reader);
                var length = reader.ReadUInt32();
                var offset = stream.Position;
                var next = offset + length + (length & 1);
                if (next > limit) throw Invalid();
                if (id == "fmt ")
                {
                    if (hasFormat || length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1) throw Invalid();
                    sampleRate = reader.ReadInt32();
                    var byteRate = reader.ReadInt32();
                    if (sampleRate is < 8000 or > 192000 || byteRate != sampleRate * 2 ||
                        reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16) throw Invalid();
                    hasFormat = true;
                }
                else if (id == "data")
                {
                    if (dataOffset >= 0 || length % 2 != 0) throw Invalid();
                    dataOffset = offset; sampleCount = length / 2;
                }
                stream.Position = next;
            }
            if (!hasFormat || dataOffset < 0 || sampleCount == 0) throw Invalid();
            return new(stream, dataOffset, sampleCount, sampleRate, hash);
        }
        catch (EndOfStreamException exception)
        {
            await stream.DisposeAsync();
            throw new InvalidDataException("補完用の音声ファイルが途中で切れています。", exception);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public PitchEvidence Evaluate(double timeSec, double f0, double step)
    {
        if (!double.IsFinite(timeSec) || timeSec < 0 || !double.IsFinite(f0) || f0 is < 60 or > 1200)
            return default;
        var first = (long)Math.Round(timeSec * _sampleRate);
        var count = (int)Math.Ceiling(Math.Max(0.04, 3.0 / f0) * _sampleRate);
        var hop = (int)Math.Round(step * _sampleRate);
        if (first < 0 || first + count > _sampleCount || hop < 1 || hop > count) return default;
        var lag = (int)Math.Round(_sampleRate / f0);
        var samples = new double[count];
        _stream.Position = _dataOffset + first * 2;
        using var reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
        for (var i = 0; i < count; i++) samples[i] = reader.ReadInt16() / 32768.0;
        var mean = samples.Average();
        for (var i = 0; i < count; i++) samples[i] -= mean;
        // 窓の後半に別の有声音があっても、欠損フレーム自身が無音なら補完しない。
        var hopMean = samples.Take(hop).Average();
        var rms = Math.Sqrt(samples.Take(hop).Sum(x => (x - hopMean) * (x - hopMean)) / hop);
        double numerator = 0, a = 0, b = 0;
        for (var i = 0; i + lag < count; i++)
        {
            var x = samples[i]; var y = samples[i + lag];
            numerator += x * y; a += x * x; b += y * y;
        }
        var denominator = Math.Sqrt(a * b);
        var periodicity = denominator > 1e-12 ? Math.Clamp(numerator / denominator, -1, 1) : 0;
        return new(periodicity, rms);
    }

    public async Task VerifyUnchangedAsync(CancellationToken token)
    {
        _stream.Position = 0;
        var current = Convert.ToHexStringLower(await SHA256.HashDataAsync(_stream, token));
        if (current != ContentHash) throw new IOException("補完処理中に元音声が変更されました。再試行してください。");
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
    private static string Four(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    private static InvalidDataException Invalid() => new("補完用の音声は有効なモノラルPCM16 WAVである必要があります。");
}
