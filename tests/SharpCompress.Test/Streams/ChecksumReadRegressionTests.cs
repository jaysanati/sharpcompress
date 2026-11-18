using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.GZip;
using SharpCompress.Crypto;
using Xunit;

namespace SharpCompress.Test.Streams;

public class ChecksumReadRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Missing_Checksum_Still_Validates_Size(bool useAsync, bool truncated)
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        using var stream = new ChecksumValidationStream(
            input,
            default,
            "test",
            expectedSize: truncated ? 4 : 3
        );
        using var output = new MemoryStream();
        if (useAsync)
        {
            if (truncated)
            {
                await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    stream.CopyToAsync(output, 81920, CancellationToken.None)
                );
            }
            else
            {
                await stream.CopyToAsync(output, 81920, CancellationToken.None);
            }
        }
        else if (truncated)
        {
            Assert.Throws<EndOfStreamException>(() => stream.CopyTo(output));
        }
        else
        {
            stream.CopyTo(output);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stored_Zero_Checksum_Is_Validated(bool useAsync)
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        using var stream = new ChecksumValidationStream(
            input,
            new ChecksumDescriptor(ChecksumKind.Crc32, 0, true),
            "test",
            expectedSize: 3
        );
        if (useAsync)
        {
            await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
                stream.CopyToAsync(Stream.Null, 81920, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<ChecksumMismatchException>(() => stream.CopyTo(Stream.Null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_Length_Read_Does_Not_Validate(bool gzip)
    {
        byte[] payload = [1, 2, 3];
        var crc = Crc32Stream.Compute(payload);
        using var input = new MemoryStream(payload);
        using var raw = new MemoryStream();
        using Stream stream = gzip
            ? new GZipChecksumValidationStream(input, raw, "test", crc, 3)
            : new ChecksumValidationStream(
                input,
                new ChecksumDescriptor(ChecksumKind.Crc32, crc, true),
                "test"
            );
        Assert.Equal(0, stream.Read(Array.Empty<byte>(), 0, 0));
        Assert.Equal(0, await stream.ReadAsync(Array.Empty<byte>(), 0, 0, CancellationToken.None));
#if !LEGACY_DOTNET
        Assert.Equal(0, stream.Read(Span<byte>.Empty));
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, CancellationToken.None));
#endif
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, 81920, CancellationToken.None);
        Assert.Equal(payload, output.ToArray());
    }
}
