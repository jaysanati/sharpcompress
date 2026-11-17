using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipHugeSolidTests
{
    public sealed class HugeTheoryAttribute : TheoryAttribute
    {
        public HugeTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("SHARPCOMPRESS_RUN_HUGE_TESTS") != "1")
            {
                Skip = "Set SHARPCOMPRESS_RUN_HUGE_TESTS=1 to run the >2 GiB solid round trip.";
            }
        }
    }

    [HugeTheory]
    [InlineData(LzmaMatchFinder.HC4)]
    [InlineData(LzmaMatchFinder.BT4)]
    public void SolidFolder_PositionNormalization_RoundTrip(LzmaMatchFinder finder)
    {
        const long size = (1L << 31) + (2 << 20);
        var path = Path.Combine(
            Path.GetTempPath(),
            "sharpcompress-huge-" + Guid.NewGuid().ToString("N") + ".7z"
        );
        try
        {
            using (var output = File.Create(path))
            using (
                var writer = new SevenZipWriter(
                    output,
                    new SevenZipWriterOptions
                    {
                        EnableParallelism = false,
                        LzmaProperties = new LzmaEncoderProperties(
                            false,
                            1 << 20,
                            32,
                            finder,
                            LzmaEncoderMode.Fast
                        ),
                    }
                )
            )
            {
                writer.BeginSolidBlock(new SevenZipSolidBlockOptions { ExpectedSize = size });
                using var input = new PatternStream(size);
                writer.Write("pattern", input, (DateTime?)null);
                writer.EndSolidBlock();
            }
            using (var archive = SevenZipArchive.OpenArchive(path))
            using (var decoded = archive.Entries.Single().OpenEntryStream())
            {
                var buffer = new byte[65536];
                long position = 0;
                int read;
                while ((read = decoded.Read(buffer, 0, buffer.Length)) != 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] != PatternStream.At(position + i))
                        {
                            throw new InvalidDataException($"Mismatch at {position + i}.");
                        }
                    }
                    position += read;
                }
                Assert.Equal(size, position);
            }
            const string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
            if (File.Exists(sevenZip))
            {
                using var process = Process.Start(
                    new ProcessStartInfo(sevenZip, "t \"" + path + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                )!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class PatternStream(long length) : Stream
    {
        private long position;

        internal static byte At(long offset) => (byte)((offset % 65521) ^ (offset >> 20));

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, length - position);
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] = At(position + i);
            }
            position += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
