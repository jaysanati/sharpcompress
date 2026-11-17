using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Crypto;

namespace SharpCompress.Writers.SevenZip;

internal sealed class SevenZipUpdateCrcStream(
    Stream source,
    uint? expected,
    string name,
    long length
) : Stream
{
    private uint seed = Crc32Stream.DEFAULT_SEED;

    private int Check(byte[] buffer, int offset, int count)
    {
        seed = ~Crc32Stream.Compute(
            Crc32Stream.DEFAULT_POLYNOMIAL,
            seed,
            buffer.AsSpan(offset, count)
        );
        if (source.Position == length && expected is uint crc && ~seed != crc)
        {
            throw new ChecksumMismatchException($"7z source CRC mismatch for '{name}'.");
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Check(buffer, offset, source.Read(buffer, offset, count));

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) =>
        Check(
            buffer,
            offset,
            await source.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false)
        );

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => source.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
