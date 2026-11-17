using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.PPMd;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipPpmdIntegrityTests : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZip_Ppmd_Corruption_Is_Sticky_Data_Error(bool asynchronous)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "7Zip.PPMd.7z"));
        for (var i = 32; i < 37; i++)
        {
            bytes[i] = 0xFF;
        }
        using var input = new MemoryStream(bytes);
        using var archive = asynchronous
            ? (SevenZipArchive)
                await SevenZipArchive.OpenAsyncArchive(
                    input,
                    cancellationToken: CancellationToken.None
                )
            : (SevenZipArchive)SevenZipArchive.OpenArchive(input);
        var entry = asynchronous
            ? (await archive.EntriesAsync.ToListAsync()).First(e => !e.IsDirectory)
            : archive.Entries.First(e => !e.IsDirectory);
        using var stream = asynchronous
            ? await entry.OpenEntryStreamAsync(CancellationToken.None)
            : entry.OpenEntryStream();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (asynchronous)
            {
                await Assert.ThrowsAsync<DataErrorException>(async () =>
                    await stream.CopyToAsync(Stream.Null, 32, CancellationToken.None)
                );
            }
            else
            {
                Assert.Throws<DataErrorException>(() => stream.CopyTo(Stream.Null));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenZip_Ppmd_Array_Read_Error(bool asynchronous)
    {
        using var input = new MemoryStream(new byte[] { 255, 255, 255, 255, 255 });
        var properties = new PpmdProperties(new byte[] { 6, 0, 0, 16, 0 });
        using var stream = asynchronous
            ? await PpmdStream.CreateAsync(properties, input, false, CancellationToken.None)
            : PpmdStream.Create(properties, input, false);
        var buffer = new byte[16];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (asynchronous)
            {
                await Assert.ThrowsAsync<DataErrorException>(async () =>
                    await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                );
            }
            else
            {
                Assert.Throws<DataErrorException>(() => stream.Read(buffer, 0, buffer.Length));
            }
        }
    }
}
