using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using SharpCompress.Test.Streams;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

[Collection(DictionarySizeLimitCollection.Name)]
public class SevenZipWriterDictionaryLimitTests
{
    [Theory]
    [InlineData(CompressionType.LZMA, 0, false)]
    [InlineData(CompressionType.LZMA, 0, true)]
    [InlineData(CompressionType.LZMA, 1, false)]
    [InlineData(CompressionType.LZMA, 1, true)]
    [InlineData(CompressionType.LZMA, 2, false)]
    [InlineData(CompressionType.LZMA, 2, true)]
    [InlineData(CompressionType.LZMA2, 0, false)]
    [InlineData(CompressionType.LZMA2, 0, true)]
    [InlineData(CompressionType.LZMA2, 1, false)]
    [InlineData(CompressionType.LZMA2, 1, true)]
    [InlineData(CompressionType.LZMA2, 2, false)]
    [InlineData(CompressionType.LZMA2, 2, true)]
    public async Task Writer_Does_Not_Apply_Decoder_Limits(
        CompressionType compression,
        int mode,
        bool useAsync
    )
    {
        var previousLzma = DictionarySizeLimits.Lzma;
        var previousLzma2 = DictionarySizeLimits.Lzma2;
        using var output = new MemoryStream();
        var expected = new byte[16384];
        try
        {
            DictionarySizeLimits.Lzma = 1;
            DictionarySizeLimits.Lzma2 = 1;
            var options = new SevenZipWriterOptions(compression)
            {
                LzmaProperties = new LzmaEncoderProperties(false, 8192),
                Solid = mode == 1,
                EnableParallelism = mode == 2,
                MaxThreads = 2,
                CompressHeader = false,
                LeaveStreamOpen = true,
            };
            using var input = new MemoryStream(expected);
            if (useAsync)
            {
                await using var writer = new SevenZipWriter(output, options);
                await writer.WriteAsync("data", input, null, CancellationToken.None);
            }
            else
            {
                using var writer = new SevenZipWriter(output, options);
                writer.Write("data", input, null);
            }

            output.Position = 0;
            using var archive = SevenZipArchive.OpenArchive(
                output,
                ReaderOptions.ForExternalStream
            );
            var entry = archive.Entries.Single();
            Assert.Throws<InvalidFormatException>(() =>
            {
                using var decoded = entry.OpenEntryStream();
            });
        }
        finally
        {
            DictionarySizeLimits.Lzma = previousLzma;
            DictionarySizeLimits.Lzma2 = previousLzma2;
        }

        output.Position = 0;
        using var reopened = SevenZipArchive.OpenArchive(output, ReaderOptions.ForExternalStream);
        using var restored = new MemoryStream();
        using var source = reopened.Entries.Single().OpenEntryStream();
        source.CopyTo(restored);
        Assert.Equal(expected, restored.ToArray());
    }
}
