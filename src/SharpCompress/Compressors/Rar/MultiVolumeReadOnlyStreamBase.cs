using System.IO;
using System.Security.Cryptography;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal abstract class MultiVolumeReadOnlyStreamBase : Stream
{
    protected Rar5CryptoInfo? CurrentCryptoInfo { get; set; }

    internal byte[] ConvertHashToMac(byte[] hash)
    {
        if (CurrentCryptoInfo is not { UseHashMac: true } info)
        {
            return hash;
        }
        if (info.HashKey is null)
        {
            throw new SharpCompress.Common.CryptographicException(
                "RAR checksum key is unavailable."
            );
        }
        // UnRAR ConvertHashToMAC hashes the finalized digest; CRC32 folds all 32 MAC bytes.
        using var hmac = new HMACSHA256(info.HashKey);
        var digest = hmac.ComputeHash(hash);
        if (hash.Length != 4)
        {
            return digest;
        }
        var folded = new byte[4];
        for (var i = 0; i < digest.Length; i++)
        {
            folded[i & 3] ^= digest[i];
        }
        return folded;
    }

    public byte[]? CurrentCrc { get; protected set; }
}
