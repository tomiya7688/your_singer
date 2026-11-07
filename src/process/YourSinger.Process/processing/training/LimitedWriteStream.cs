using System.IO;

namespace YourSinger.Process.Processing.Training;

// { 責務: [LimitedWriteStream: 単一のJSON書き手による上限超過を事前に拒否する] }
internal sealed class LimitedWriteStream(Stream target, long limit) : Stream
{
    private long written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => target.CanWrite;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }

    // { 責務: [RequireCapacity: 上限を超える書き込みを拒否する] }
    private void RequireCapacity(int count)
    {
        if (count > limit - written)
            throw new InvalidDataException("歌唱学習datasetが大きすぎます。対象を分割してください。");
    }

    // { 責務: [Write: 上限内のバイト列を書き込む] }
    public override void Write(byte[] buffer, int offset, int count)
    {
        RequireCapacity(count);
        target.Write(buffer, offset, count);
        written += count;
    }

    // { 責務: [WriteAsync: 上限内のバイト列を非同期に書き込む] }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        RequireCapacity(buffer.Length);
        await target.WriteAsync(buffer, cancellationToken);
        written += buffer.Length;
    }

    // { 責務: [WriteAsync: 配列からの非同期書き込みを同じ上限に通す] }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => target.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
