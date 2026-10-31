using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipDictionaryTests
{
    [Theory]
    [InlineData(CompressionType.LZMA, false)]
    [InlineData(CompressionType.LZMA, true)]
    [InlineData(CompressionType.LZMA2, false)]
    [InlineData(CompressionType.LZMA2, true)]
    public async Task SevenZipWriter_Dictionary_MatchesRemainingInput(
        CompressionType method,
        bool asynchronous
    )
    {
        await CheckDictionaryAsync(method, asynchronous, 10240, 64 << 20, 12288, 0, true);
        await CheckDictionaryAsync(method, asynchronous, 10240, 4096, 4096, 0, true);
        await CheckDictionaryAsync(method, asynchronous, 20000, 64 << 20, 12288, 9760, true);
        await CheckDictionaryAsync(method, asynchronous, 10240, 16384, 16384, 0, false);
        await CheckDictionaryAsync(method, asynchronous, 1, 64 << 20, 4096, 0, true);
    }

    private static async Task CheckDictionaryAsync(
        CompressionType method,
        bool asynchronous,
        int length,
        int configured,
        int expected,
        int position,
        bool seekable
    )
    {
        var content = new byte[length];
        new Random(318).NextBytes(content);
        using var source = new InputStream(content, seekable);
        source.Position = position;
        using var output = new MemoryStream();
        var options = new SevenZipWriterOptions(method)
        {
            LzmaProperties = new LzmaEncoderProperties(true, configured, 64),
            LeaveStreamOpen = true,
        };
        if (asynchronous)
        {
            await using var writer = new SevenZipWriter(output, options);
            await writer.WriteAsync("data.bin", source, null, CancellationToken.None);
        }
        else
        {
            using var writer = new SevenZipWriter(output, options);
            writer.Write("data.bin", source, null);
        }
        Assert.True(output.CanWrite);
        Assert.True(source.CanRead);
        output.Position = 0;
        using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(output);
        var entry = Assert.Single(archive.Entries);
        var properties = Assert.Single(entry.FilePart.Folder!._coders)._props!;
        var dictionary =
            method == CompressionType.LZMA
                ? BitConverter.ToInt32(properties, 1)
                : (2 | (properties[0] & 1)) << ((properties[0] >> 1) + 11);
        Assert.Equal(expected, dictionary);
        using var decoded = entry.OpenEntryStream();
        using var restored = new MemoryStream();
        decoded.CopyTo(restored);
        Assert.Equal(content.Skip(position).ToArray(), restored.ToArray());
    }

    private sealed class InputStream(byte[] content, bool seekable) : MemoryStream(content)
    {
        public override bool CanSeek => seekable;
    }
}
