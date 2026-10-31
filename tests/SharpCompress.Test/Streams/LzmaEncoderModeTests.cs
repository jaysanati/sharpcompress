using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.Streams;

public class LzmaEncoderModeTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (
            var finder in new[] { LzmaMatchFinder.BT2, LzmaMatchFinder.BT4, LzmaMatchFinder.HC4 }
        )
        {
            foreach (var mode in new[] { LzmaEncoderMode.Fast, LzmaEncoderMode.Normal })
            {
                foreach (var lzma2 in new[] { false, true })
                {
                    foreach (var size in new[] { 0, 1, 4096, (2 << 20) + 1, 8 << 20 })
                    {
                        foreach (var kind in new[] { 0, 1, 2 })
                        {
                            yield return new object[] { finder, mode, lzma2, size, kind };
                        }
                    }
                }
            }
        }
    }

    private static byte[] Data(int size, int kind)
    {
        var data = new byte[size];
        new Random(12345).NextBytes(data);
        var text =
            "LZMA fast parsing and hash chains preserve dictionary history across chunks.\n"u8;
        for (var i = 0; i < size; i++)
        {
            if (kind == 1 || (kind == 2 && (i / (1 << 20)) % 2 != 0))
            {
                data[i] = text[i % text.Length];
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Stream_RoundTrip(
        LzmaMatchFinder finder,
        LzmaEncoderMode mode,
        bool lzma2,
        int size,
        int kind
    )
    {
        var data = Data(size, kind);
        var properties = new LzmaEncoderProperties(!lzma2, 1 << 20, 32, finder, mode);
        var sync = await StreamRoundTrip(data, properties, lzma2, false);
        var asyncBytes = await StreamRoundTrip(data, properties, lzma2, true);
        Assert.Equal(sync, asyncBytes);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Archive_RoundTrip(
        LzmaMatchFinder finder,
        LzmaEncoderMode mode,
        bool lzma2,
        int size,
        int kind
    )
    {
        var data = Data(size, kind);
        var properties = new LzmaEncoderProperties(!lzma2, 1 << 20, 32, finder, mode);
        var sync = await ArchiveRoundTrip(data, properties, lzma2, false);
        var asyncBytes = await ArchiveRoundTrip(data, properties, lzma2, true);
        Assert.Equal(sync, asyncBytes);
    }

    private static async Task<byte[]> StreamRoundTrip(
        byte[] data,
        LzmaEncoderProperties properties,
        bool lzma2,
        bool useAsync
    )
    {
        using var packed = new MemoryStream();
        using var asyncOutput = new AsyncOnlyStream(packed, disposeStream: false);
        var encoder = LzmaStream.Create(properties, lzma2, useAsync ? asyncOutput : packed);
        var decoderProperties = encoder.Properties;
        // End-marked raw LZMA is decoded without an external uncompressed size.
        // This also consumes the marker for an empty stream.
        var decodedSize =
            !lzma2 && (bool)properties.Properties[properties.Properties.Length - 1]
                ? -1
                : data.Length;
        Assert.True(encoder.CanWrite);
        Assert.False(encoder.CanRead);
        if (useAsync)
        {
            await using (encoder)
            {
                // Exercise both async write overloads and incremental input.
                var split = data.Length / 2;
                await encoder.WriteAsync(data, 0, split, CancellationToken.None);
#if LEGACY_DOTNET
                await encoder.WriteAsync(data, split, data.Length - split, CancellationToken.None);
#else
                await encoder.WriteAsync(data.AsMemory(split), CancellationToken.None);
#endif
            }
        }
        else
        {
            using (encoder)
            {
                var split = data.Length / 2;
                encoder.Write(data, 0, split);
                encoder.Write(data, split, data.Length - split);
            }
        }
        Assert.False(encoder.CanWrite);
        Assert.True(packed.CanWrite);
        var bytes = packed.ToArray();
        packed.Position = 0;
        using var extracted = new MemoryStream();
        if (useAsync)
        {
            await using var decoder = await LzmaStream.CreateAsync(
                decoderProperties,
                packed,
                bytes.Length,
                decodedSize,
                leaveOpen: true
            );
            await decoder.CopyToAsync(extracted, 65536, CancellationToken.None);
        }
        else
        {
            using var decoder = LzmaStream.Create(
                decoderProperties,
                packed,
                bytes.Length,
                decodedSize,
                leaveOpen: true
            );
            decoder.CopyTo(extracted);
        }
        Assert.Equal(data, extracted.ToArray());
        Assert.True(packed.CanRead);
        return bytes;
    }

    private static async Task<byte[]> ArchiveRoundTrip(
        byte[] data,
        LzmaEncoderProperties properties,
        bool lzma2,
        bool useAsync
    )
    {
        using var packed = new MemoryStream();
        using var input = new MemoryStream(data);
        using var asyncInput = new AsyncOnlyStream(input, disposeStream: false);
        using var asyncOutput = new AsyncOnlyStream(packed, disposeStream: false);
        var options = new SevenZipWriterOptions(
            lzma2 ? CompressionType.LZMA2 : CompressionType.LZMA
        )
        {
            LzmaProperties = properties,
            LeaveStreamOpen = true,
            CompressHeader = false,
        };
        var writer = new SevenZipWriter(useAsync ? asyncOutput : packed, options);
        if (useAsync)
        {
            await using (writer)
            {
                await writer.WriteAsync("data.bin", asyncInput, null, CancellationToken.None);
            }
        }
        else
        {
            using (writer)
            {
                writer.Write("data.bin", input, null);
            }
        }
        Assert.True(input.CanRead);
        Assert.True(packed.CanWrite);
        var bytes = packed.ToArray();
        packed.Position = 0;
        using var extracted = new MemoryStream();
        if (useAsync)
        {
            await using var archive = await SevenZipArchive.OpenAsyncArchive(
                packed,
                ReaderOptions.ForExternalStream,
                CancellationToken.None
            );
            var entry = Assert.Single(await archive.EntriesAsync.ToListAsync());
            using var stream = await entry.OpenEntryStreamAsync(CancellationToken.None);
            await stream.CopyToAsync(extracted, 65536, CancellationToken.None);
        }
        else
        {
            using var archive = SevenZipArchive.OpenArchive(
                packed,
                ReaderOptions.ForExternalStream
            );
            using var stream = Assert.Single(archive.Entries).OpenEntryStream();
            stream.CopyTo(extracted);
        }
        Assert.Equal(data, extracted.ToArray());
        Assert.True(packed.CanRead);
        return bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_Is_Byte_Identical_To_Explicit_BT4_Normal(bool useAsync)
    {
        var data = Data(65536, 1);
        var defaults = new[]
        {
            new LzmaEncoderProperties(),
            new LzmaEncoderProperties(false),
            new LzmaEncoderProperties(false, 1 << 20),
            new LzmaEncoderProperties(false, 1 << 20, 32),
        };
        var explicitProperties = new LzmaEncoderProperties(
            false,
            1 << 20,
            32,
            LzmaMatchFinder.BT4,
            LzmaEncoderMode.Normal
        );
        foreach (var lzma2 in new[] { false, true })
        {
            var expected = await StreamRoundTrip(data, explicitProperties, lzma2, useAsync);
            var expectedArchive = await ArchiveRoundTrip(data, explicitProperties, lzma2, useAsync);
            foreach (var properties in defaults)
            {
                Assert.Equal(expected, await StreamRoundTrip(data, properties, lzma2, useAsync));
                Assert.Equal(
                    expectedArchive,
                    await ArchiveRoundTrip(data, properties, lzma2, useAsync)
                );
            }
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    public void Invalid_Public_Values_Are_Rejected(int finder, int mode)
    {
        Assert.Throws<InvalidParamException>(() =>
            new LzmaEncoderProperties(
                false,
                4096,
                32,
                (LzmaMatchFinder)finder,
                (LzmaEncoderMode)mode
            )
        );
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData("fast")]
    public void Invalid_Algorithm_Is_Rejected(object algorithm)
    {
        using var encoder = new SharpCompress.Compressors.LZMA.Encoder();
        Assert.Throws<InvalidParamException>(() =>
            encoder.SetCoderProperties(new[] { CoderPropId.Algorithm }, new[] { algorithm })
        );
    }

    [Fact]
    public void Algorithm_Two_Is_Normal()
    {
        var data = Data(65536, 1);
        byte[] Encode(int algorithm)
        {
            using var encoder = new SharpCompress.Compressors.LZMA.Encoder();
            var properties = new LzmaEncoderProperties(true, 4096, 32);
            encoder.SetCoderProperties(properties.PropIDs, properties.Properties);
            encoder.SetCoderProperties(new[] { CoderPropId.Algorithm }, new object[] { algorithm });
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            encoder.Code(input, output, -1, -1, null);
            return output.ToArray();
        }
        Assert.Equal(Encode(1), Encode(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_And_NonSeekable_Input(bool lzma2)
    {
        var properties = new LzmaEncoderProperties(
            !lzma2,
            4096,
            32,
            LzmaMatchFinder.HC4,
            LzmaEncoderMode.Fast
        );
        using var packed = new MemoryStream();
        await using var encoder = LzmaStream.Create(properties, lzma2, packed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            encoder.WriteAsync(new byte[1], 0, 1, cancellation.Token)
        );
        using var source = new MemoryStream(Data(65536, 2));
        using var nonSeekable = new ShortReadStream(source, 997, seekable: false);
        await nonSeekable.CopyToAsync(encoder, 4096, CancellationToken.None);
    }

    [Theory]
    [InlineData("HC3")]
    [InlineData("")]
    [InlineData(4)]
    public void Invalid_MatchFinder_Is_Rejected(object finder)
    {
        using var encoder = new SharpCompress.Compressors.LZMA.Encoder();
        Assert.Throws<InvalidParamException>(() =>
            encoder.SetCoderProperties(new[] { CoderPropId.MatchFinder }, new[] { finder })
        );
    }

    [Fact]
    public void Lzma2_Preset_Dictionary_Is_Rejected_Without_Closing_Streams()
    {
        using var output = new MemoryStream();
        using var dictionary = new MemoryStream(new byte[32]);
        Assert.Throws<InvalidParamException>(() =>
            LzmaStream.Create(new LzmaEncoderProperties(), true, dictionary, output)
        );
        Assert.True(output.CanWrite);
        Assert.True(dictionary.CanRead);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("16")]
    public void Invalid_MatchFinder_Cycles_Are_Rejected(object cycles)
    {
        using var encoder = new SharpCompress.Compressors.LZMA.Encoder();
        Assert.Throws<InvalidParamException>(() =>
            encoder.SetCoderProperties(new[] { CoderPropId.MatchFinderCycles }, new[] { cycles })
        );
    }

    [Theory]
    [InlineData("BT2")]
    [InlineData("BT4")]
    [InlineData("HC4")]
    public void Explicit_MatchFinder_Cycles_RoundTrip(string finder)
    {
        var data = Data(65536, 0);
        using var encoder = new SharpCompress.Compressors.LZMA.Encoder();
        encoder.SetCoderProperties(
            new[]
            {
                CoderPropId.MatchFinder,
                CoderPropId.MatchFinderCycles,
                CoderPropId.Algorithm,
                CoderPropId.EndMarker,
            },
            new object[] { finder, 1, 0, true }
        );
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        encoder.Code(input, output, -1, -1, null);
        var properties = new byte[5];
        encoder.WriteCoderProperties(properties);
        output.Position = 0;
        using var decoder = LzmaStream.Create(properties, output, leaveOpen: true);
        using var decoded = new MemoryStream();
        decoder.CopyTo(decoded);
        Assert.Equal(data, decoded.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fast_Parsing_Is_Distinct_From_Normal(bool lzma2)
    {
        var random = new Random(42);
        var data = new byte[65536];
        // A small alphabet gives many competing short matches and look-ahead decisions.
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)random.Next(8);
        }
        var fast = new LzmaEncoderProperties(
            !lzma2,
            4096,
            32,
            LzmaMatchFinder.HC4,
            LzmaEncoderMode.Fast
        );
        var normal = new LzmaEncoderProperties(
            !lzma2,
            4096,
            32,
            LzmaMatchFinder.HC4,
            LzmaEncoderMode.Normal
        );
        var fastBytes = await StreamRoundTrip(data, fast, lzma2, false);
        var normalBytes = await StreamRoundTrip(data, normal, lzma2, false);
        Assert.False(fastBytes.SequenceEqual(normalBytes));
        Assert.Equal(fastBytes, await StreamRoundTrip(data, fast, lzma2, true));
        Assert.Equal(
            await ArchiveRoundTrip(data, fast, lzma2, false),
            await ArchiveRoundTrip(data, fast, lzma2, true)
        );
    }
}
