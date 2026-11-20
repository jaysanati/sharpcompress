using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Streams;

public class Lzma1AsyncBufferingTests : TestBase
{
    private static (byte[] Properties, byte[] Packed, byte[] Data) Encode(int size, bool marker)
    {
        var data = new byte[size];
        new Random(343).NextBytes(data);
        using var packed = new MemoryStream();
        var encoder = LzmaStream.Create(new LzmaEncoderProperties(marker, 4096), false, packed);
        var properties = encoder.Properties;
        using (encoder)
        {
            encoder.Write(data, 0, data.Length);
        }
        return (properties, packed.ToArray(), data);
    }

    [Theory]
    [InlineData(0, true, 1)]
    [InlineData(1, true, 7)]
    [InlineData(9, false, 1)]
    [InlineData(400000, true, 1)]
    [InlineData(400000, true, 7)]
    [InlineData(400000, true, 81920)]
    [InlineData(400000, false, 81920)]
    public async Task BoundedInputMatchesSync(int size, bool marker, int readSize)
    {
        var (properties, packed, data) = Encode(size, marker);
        using var source = new MemoryStream(packed);
        using var sync = LzmaStream.Create(properties, source, packed.Length, marker ? -1 : size);
        using var expected = new MemoryStream();
        sync.CopyTo(expected);
        using var input = new RecordingInput(packed.Concat(new byte[] { 123, 45 }).ToArray());
        using var decoder = await LzmaStream.CreateAsync(
            properties,
            input,
            packed.Length,
            marker ? -1 : size,
            leaveOpen: true
        );
        using var actual = new MemoryStream();
        var buffer = new byte[readSize];
        int read;
        while (
            (read = await decoder.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) != 0
        )
        {
            actual.Write(buffer, 0, read);
        }
        Assert.Equal(data, actual.ToArray());
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(packed.Length, input.Position);
#if !LEGACY_DOTNET
        if (size > 65536)
        {
            Assert.InRange(input.MaxRequested, 65000, 65536);
            Assert.InRange(input.Calls, 6, 100);
        }
#endif
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task CompressedLimitShorterThanInitializationMatchesSync(int limit)
    {
        var (properties, packed, _) = Encode(1000, true);
        using var source = new MemoryStream(packed);
        using var sync = LzmaStream.Create(properties, source, limit, -1);
        var expected = Record.Exception(() => sync.CopyTo(Stream.Null));
        Assert.NotNull(expected);
        using var input = new RecordingInput(packed);
        using var decoder = await LzmaStream.CreateAsync(properties, input, limit, -1);
        var actual = await Record.ExceptionAsync(() =>
            decoder.CopyToAsync(Stream.Null, 81920, CancellationToken.None)
        );
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
#if !LEGACY_DOTNET
        Assert.Equal(5, input.Position);
#endif
    }

    [Fact]
    public async Task UnknownCompressedSizeLeavesNextPayloadUnread()
    {
        var (properties, packed, data) = Encode(1000, true);
        using var input = new RecordingInput(packed.Concat(packed).ToArray());
        for (var i = 1; i <= 2; i++)
        {
            using var decoder = await LzmaStream.CreateAsync(
                properties,
                input,
                -1,
                -1,
                leaveOpen: true
            );
            using var output = new MemoryStream();
            await decoder.CopyToAsync(output, 81920, CancellationToken.None);
            Assert.Equal(data, output.ToArray());
            Assert.Equal(packed.Length * i, input.Position);
        }
        Assert.Equal(1, input.MaxRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInputMatchesSyncAndCannotRetry(bool corrupt)
    {
        var (properties, packed, data) = Encode(10000, true);
        if (corrupt)
        {
            for (var i = 5; i < 55; i++)
            {
                packed[i] = 255;
            }
        }
        else
        {
            packed = packed.Take(packed.Length - 20).ToArray();
        }
        using var source = new MemoryStream(packed);
        using var sync = LzmaStream.Create(properties, source, packed.Length, -1);
        var expected = Record.Exception(() => sync.CopyTo(Stream.Null));
        Assert.NotNull(expected);
        using var input = new RecordingInput(packed);
        using var decoder = await LzmaStream.CreateAsync(properties, input, packed.Length, -1);
        var actual = await Record.ExceptionAsync(() =>
            decoder.CopyToAsync(Stream.Null, 81920, CancellationToken.None)
        );
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        var calls = input.Calls;
        var retry = await Record.ExceptionAsync(() =>
            decoder.ReadAsync(new byte[1], 0, 1, CancellationToken.None)
        );
        Assert.Same(actual, retry);
        Assert.Equal(calls, input.Calls);
    }

#if !LEGACY_DOTNET
    [Fact]
    public async Task CancellationDuringLaterRefillPoisonsDecoder()
    {
        var (properties, packed, data) = Encode(400000, true);
        using var input = new RecordingInput(packed);
        using var decoder = await LzmaStream.CreateAsync(properties, input, packed.Length, -1);
        using var cancellation = new CancellationTokenSource();
        input.AfterRead = () =>
        {
            if (input.Position > 65541)
            {
                cancellation.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decoder.CopyToAsync(Stream.Null, 81920, cancellation.Token)
        );
        var calls = input.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decoder.ReadAsync(new byte[1], 0, 1, CancellationToken.None)
        );
        Assert.Equal(calls, input.Calls);
    }
#endif

    [Theory]
    [InlineData("7Zip.LZMA.7z")]
    [InlineData("7Zip.LZMA.Aes.7z")]
    [InlineData("7Zip.BCJ.7z")]
    [InlineData("7Zip.BCJ2.7z")]
    public async Task ArchiveEntryStreamsMatchSync(string name)
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, name);
        using var sync = ArchiveFactory.OpenArchive(
            path,
            new ReaderOptions { Password = "testpassword" }
        );
        var entries = sync.Entries.Where(e => !e.IsDirectory).ToArray();
        await using var archive = await ArchiveFactory.OpenAsyncArchive(
            path,
            new ReaderOptions { Password = "testpassword" }
        );
        var index = 0;
        await foreach (var entry in archive.EntriesAsync)
        {
            if (entry.IsDirectory)
            {
                continue;
            }
            using var expected = new MemoryStream();
            using (var stream = entries[index++].OpenEntryStream())
            {
                stream.CopyTo(expected);
            }
            using var actual = new MemoryStream();
            using (var stream = await entry.OpenEntryStreamAsync(CancellationToken.None))
            {
                await stream.CopyToAsync(actual, 81920, CancellationToken.None);
            }
            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
        Assert.Equal(entries.Length, index);
    }

    private sealed class RecordingInput(byte[] data)
        : MemoryStream(data, 0, data.Length, false, true)
    {
        public int Calls { get; private set; }
        public int MaxRequested { get; private set; }
        public Action? AfterRead { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous input read");

        public override int ReadByte() =>
            throw new InvalidOperationException("Synchronous input read");

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            MaxRequested = Math.Max(MaxRequested, count);
            var read = base.Read(buffer, offset, Math.Min(count, 7000));
            AfterRead?.Invoke();
            return Task.FromResult(read);
        }

#if !LEGACY_DOTNET
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            MaxRequested = Math.Max(MaxRequested, buffer.Length);
            var read = (int)Math.Min(Math.Min(buffer.Length, 7000), Length - Position);
            GetBuffer().AsSpan((int)Position, read).CopyTo(buffer.Span);
            Position += read;
            AfterRead?.Invoke();
            return new ValueTask<int>(read);
        }
#endif
    }
}
