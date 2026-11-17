using System;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarBLAKE2spStream : RarStream
{
    public static ValueTask<RarBLAKE2spStream> CreateAsync(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyAsyncStream readStream,
        CancellationToken cancellationToken = default
    )
    {
        var stream = new RarBLAKE2spStream(unpack, fileHeader, readStream);
        return new ValueTask<RarBLAKE2spStream>(stream);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var result = await base.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        if (result != 0)
        {
            Update(_blake2sp!, new ReadOnlySpan<byte>(buffer, offset, result));
        }
        VerifyHash(result, count);

        return result;
    }

#if !LEGACY_DOTNET
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var result = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result != 0)
        {
            Update(_blake2sp!, buffer.Span.Slice(0, result));
        }
        VerifyHash(result, buffer.Length);

        return result;
    }
#endif
}
