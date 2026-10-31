using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.SevenZipGate;

internal static class ManagedCompression
{
    public static void Run(Options options)
    {
        var dataset =
            options.Datasets?.Single()
            ?? throw new ArgumentException("Child requires one dataset.");
        var engine =
            options.Engines?.Single() ?? throw new ArgumentException("Child requires one engine.");
        var level =
            options.Levels?.Single() ?? throw new ArgumentException("Child requires one level.");
        var files = Paths.InputFiles(options, dataset);
        long processed = 0;
        using var progress = new Timer(
            _ => Console.WriteLine($"progress {Interlocked.Read(ref processed)}"),
            null,
            0,
            1000
        );
        if (engine == "sc-lzma-par")
        {
            long compressed = 0;
            _ = Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = options.ParallelThreads },
                file =>
                {
                    using var input = new CountingInput(
                        File.OpenRead(file),
                        count => Interlocked.Add(ref processed, count)
                    );
                    using var output = new CountingOutput();
                    using (
                        var encoder = LzmaStream.Create(
                            new LzmaEncoderProperties(
                                true,
                                ReduceDictionary(input.Length, options.Dictionary),
                                options.FastBytes
                            ),
                            false,
                            output
                        )
                    )
                    {
                        input.CopyTo(encoder);
                    }

                    _ = Interlocked.Add(ref compressed, output.Length);
                }
            );
            Console.WriteLine($"size {compressed}");
        }
        else
        {
            if (engine is not ("sc-lzma-1t" or "sc-enci-today"))
            {
                throw new ArgumentException("Unknown managed engine.");
            }

            using var output = File.Create(options.At("out", $"{dataset}-{engine}-mx{level}.7z"));
            using (
                var writer = new SevenZipWriter(
                    output,
                    new SevenZipWriterOptions(CompressionType.LZMA)
                    {
                        LzmaProperties =
                            engine == "sc-enci-today"
                                ? null
                                : new LzmaEncoderProperties(
                                    true,
                                    options.Dictionary,
                                    options.FastBytes
                                ),
                        LeaveStreamOpen = true,
                    }
                )
            )
            {
                foreach (var file in files)
                {
                    using var input = new CountingInput(
                        File.OpenRead(file),
                        count => Interlocked.Add(ref processed, count)
                    );
                    writer.Write(
                        Path.GetRelativePath(options.At("data", dataset), file).Replace('\\', '/'),
                        input,
                        File.GetLastWriteTimeUtc(file)
                    );
                }
            }
            Console.WriteLine($"size {output.Length}");
        }
        Console.WriteLine($"progress {Interlocked.Read(ref processed)}");
    }

    private static int ReduceDictionary(long size, int configured)
    {
        if (size >= configured)
        {
            return configured;
        }
        // Same candidate sequence as SevenZipStreamsCompressor. The harness uses
        // only public APIs, so the parallel estimate applies normalization here.
        for (var i = 11; i <= 30; i++)
        {
            for (var multiplier = 2L; multiplier <= 3; multiplier++)
            {
                if ((multiplier << i) >= size)
                {
                    return checked((int)(multiplier << i));
                }
            }
        }

        return configured;
    }

    private sealed class CountingInput(Stream inner, Action<int> report) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            report(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            report(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingOutput : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;

        public override void WriteByte(byte value) => length++;

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
