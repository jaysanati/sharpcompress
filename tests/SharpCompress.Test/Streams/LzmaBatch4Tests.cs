using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using Xunit;

namespace SharpCompress.Test.Streams;

public class LzmaBatch4Tests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrematureEndMarkerIsSticky(bool asynchronous, bool memoryRead)
    {
        using var packed = new MemoryStream();
        byte[] properties;
        using (
            var encoder = LzmaStream.Create(new LzmaEncoderProperties(true, 4096), false, packed)
        )
        {
            properties = encoder.Properties;
            encoder.Write(new byte[] { 42 }, 0, 1);
        }
        using var input = new MemoryStream(packed.ToArray());
        using var decoder = asynchronous
            ? await LzmaStream.CreateAsync(properties, input, input.Length, 2, leaveOpen: true)
            : LzmaStream.Create(properties, input, input.Length, 2, leaveOpen: true);
        var buffer = new byte[16];
        var first = await Assert.ThrowsAsync<DataErrorException>(() =>
            Read(decoder, buffer, asynchronous, memoryRead)
        );
        var second = await Assert.ThrowsAsync<DataErrorException>(() =>
            Read(decoder, buffer, asynchronous, memoryRead)
        );
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedLzmaHasSameStickyException(bool asynchronous)
    {
        using var input = new MemoryStream(new byte[5]);
        byte[] properties = { 0x5D, 0, 16, 0, 0 };
        using var decoder = asynchronous
            ? await LzmaStream.CreateAsync(properties, input, -1, 100, leaveOpen: true)
            : LzmaStream.Create(properties, input, -1, 100, leaveOpen: true);
        var buffer = new byte[100];
        var first = await Assert.ThrowsAsync<IncompleteArchiveException>(() =>
            Read(decoder, buffer, asynchronous, false)
        );
        var second = await Assert.ThrowsAsync<IncompleteArchiveException>(() =>
            Read(decoder, buffer, asynchronous, false)
        );
        Assert.Same(first, second);
        Assert.Same(first, Assert.Throws<IncompleteArchiveException>(() => decoder.ReadByte()));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    public async Task ShortLzma2PackSize(bool asynchronous, byte sizeMinusOne)
    {
        using var input = new MemoryStream(
            new byte[] { 0xE0, 0, 0, 0, sizeMinusOne, 0x5D, 0, 0, 0, 0, 0 }
        );
        using var decoder = asynchronous
            ? await LzmaStream.CreateAsync(new byte[] { 0 }, input, -1, -1, leaveOpen: true)
            : LzmaStream.Create(new byte[] { 0 }, input, leaveOpen: true);
        await Assert.ThrowsAsync<DataErrorException>(() =>
            Read(decoder, new byte[1], asynchronous, false)
        );
        Assert.True(input.Position <= 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lzma2LiteralContextPlusPositionLimit(bool asynchronous)
    {
        // lc=5, lp=0 is valid for LZMA1 but forbidden for LZMA2.
        using var input = new MemoryStream(new byte[] { 0xE0, 0, 0, 0, 4, 5, 0, 0, 0, 0, 0 });
        using var decoder = asynchronous
            ? await LzmaStream.CreateAsync(new byte[] { 0 }, input, -1, -1, leaveOpen: true)
            : LzmaStream.Create(new byte[] { 0 }, input, leaveOpen: true);
        await Assert.ThrowsAsync<DataErrorException>(() =>
            Read(decoder, new byte[1], asynchronous, false)
        );
        using var rawInput = new MemoryStream(new byte[5]);
        using var raw = LzmaStream.Create(new byte[] { 5, 0, 16, 0, 0 }, rawInput, leaveOpen: true);
    }

    [Fact]
    public void ParallelMaterializationIsBoundedAndScanIsCancelable()
    {
        Assert.False(Lzma2ParallelDecoder.CanMaterialize(32, 1L << 30));
        Assert.False(Lzma2ParallelDecoder.CanMaterialize(long.MaxValue, long.MaxValue));
        Assert.True(Lzma2ParallelDecoder.CanMaterialize(1 << 20, 1 << 20));
        using var input = new MemoryStream(new byte[] { 0 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Lzma2ParallelDecoder.TryScanBlocks(input, 0, 1, 0, cancellation.Token)
        );
#if !LEGACY_DOTNET
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(-1), false);
        Assert.Throws<OperationCanceledException>(() =>
            Lzma2ParallelDecoder.DecodeBlocksParallel(
                handle,
                new byte[] { 0 },
                0,
                Array.Empty<Lzma2Block>(),
                handle,
                1,
                cancellation.Token
            )
        );
#endif
    }

    private static async Task Read(
        LzmaStream stream,
        byte[] buffer,
        bool asynchronous,
        bool memoryRead
    )
    {
        if (asynchronous)
        {
#if !LEGACY_DOTNET
            if (memoryRead)
            {
                await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                return;
            }
#endif
            await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        }
        else
        {
            stream.Read(buffer, 0, buffer.Length);
        }
    }
}
