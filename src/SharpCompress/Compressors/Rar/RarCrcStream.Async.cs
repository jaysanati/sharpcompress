using System;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarCrcStream : RarStream
{
    public static ValueTask<RarCrcStream> CreateAsync(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStreamBase readStream,
        CancellationToken cancellationToken = default
    )
    {
        var stream = new RarCrcStream(unpack, fileHeader, readStream);
        return new ValueTask<RarCrcStream>(stream);
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
            _currentCrc = RarCRC.CheckCrc(_currentCrc, buffer, offset, result);
        }
        VerifyCrc(result, count);

        return result;
    }

#if !LEGACY_DOTNET
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result != 0)
        {
            _currentCrc = RarCRC.CheckCrc(_currentCrc, buffer.Span, 0, result);
        }
        VerifyCrc(result, buffer.Length);

        return result;
    }
#endif
}
