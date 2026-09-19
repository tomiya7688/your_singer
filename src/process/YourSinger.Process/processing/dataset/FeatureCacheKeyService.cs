using System.Security.Cryptography;
using System.Text;

namespace YourSinger.Process.Processing.Dataset;

public sealed class FeatureCacheKeyService
{
    public async Task<string> CreateAsync(
        string audioPath,
        string stageVersion,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            audioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        hash.AppendData(Encoding.UTF8.GetBytes("|" + stageVersion));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
