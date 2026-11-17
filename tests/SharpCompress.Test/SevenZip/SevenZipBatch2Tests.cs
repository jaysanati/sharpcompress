using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.LZMA.LZ;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class SevenZipBatch2Tests
{
    private static SevenZipWriterOptions Options(
        CompressionType compression = CompressionType.LZMA2,
        bool parallel = false,
        string? password = null
    ) =>
        new()
        {
            CompressionType = compression,
            Solid = true,
            CompressHeader = false,
            EnableParallelism = parallel,
            MaxThreads = 2,
            Password = password,
            LzmaProperties = new LzmaEncoderProperties(false, 1 << 20),
        };

    private static MemoryStream Source(
        CompressionType compression = CompressionType.LZMA2,
        string? password = null
    )
    {
        var output = new MemoryStream();
        using (var writer = new SevenZipWriter(output, Options(compression, password: password)))
        {
            var bytes = new byte[4096];
            new Random(381).NextBytes(bytes);
            using var input = new MemoryStream(bytes);
            writer.Write("first", input, (DateTime?)null);
            new Random(382).NextBytes(bytes);
            input.Position = 0;
            writer.Write("second", input, (DateTime?)null);
        }
        output.Position = 0;
        return output;
    }

    private static void InvalidOutput(MemoryStream output)
    {
        output.Position = 0;
        Assert.ThrowsAny<Exception>(() =>
        {
            using var archive = SevenZipArchive.OpenArchive(output);
            _ = archive.Entries.ToArray();
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task Recompress_RejectsCorruptSurvivorsAndDeletedFiles(
        bool asynchronous,
        bool parallel,
        bool deleteCorrupt
    )
    {
        using var source = Source();
        // Random first chunk is stored LZMA2: skip control and two size bytes.
        Assert.Equal(1, source.GetBuffer()[32]);
        source.GetBuffer()[35 + 20] ^= 1;
        using var update = SevenZipUpdate.Open(source);
        update.Delete(deleteCorrupt ? 0 : 1);
        using var output = new MemoryStream();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<ChecksumMismatchException>(async () =>
                await update.WriteToAsync(
                    output,
                    Options(parallel: parallel),
                    cancellationToken: CancellationToken.None
                )
            );
        }
        else
        {
            Assert.Throws<ChecksumMismatchException>(() =>
                update.WriteTo(output, Options(parallel: parallel))
            );
        }
        InvalidOutput(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recompress_WrongAesCopyPassword(bool asynchronous)
    {
        using var source = Source(CompressionType.None, "correct");
        using var update = SevenZipUpdate.Open(source, new ReaderOptions { Password = "wrong" });
        update.Delete(1);
        using var output = new MemoryStream();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<ChecksumMismatchException>(async () =>
                await update.WriteToAsync(
                    output,
                    Options(password: "different"),
                    cancellationToken: CancellationToken.None
                )
            );
        }
        else
        {
            Assert.Throws<ChecksumMismatchException>(() =>
                update.WriteTo(output, Options(password: "different"))
            );
        }
        InvalidOutput(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditionsFailure_DoesNotFinalize(bool asynchronous)
    {
        using var source = Source();
        using var update = SevenZipUpdate.Open(source);
        using var output = new MemoryStream();
        var failure = new InvalidOperationException("add failed");
        if (asynchronous)
        {
            Assert.Same(
                failure,
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await update.WriteToAsync(
                        output,
                        Options(),
                        (_, _) => throw failure,
                        CancellationToken.None
                    )
                )
            );
        }
        else
        {
            Assert.Same(
                failure,
                Assert.Throws<InvalidOperationException>(() =>
                    update.WriteTo(output, Options(), _ => throw failure)
                )
            );
        }
        InvalidOutput(output);
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation)
        : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => cancellation.Cancel();
    }

    [Fact]
    public async Task RawCopyCancellation_DoesNotFinalize()
    {
        using var source = Source();
        using var update = SevenZipUpdate.Open(source);
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var options = Options();
        options.BufferSize = 64;
        options.Progress = new CancelProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await update.WriteToAsync(output, options, cancellationToken: cancellation.Token)
        );
        InvalidOutput(output);
    }

    [Fact]
    public async Task Abort_DoesNotWaitForWorkerOrWriteHeader()
    {
        using var output = new MemoryStream();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var writer = new SevenZipWriter(output, Options(parallel: true));
        writer.ParallelBeforeEncode = (_, _) =>
        {
            entered.Set();
            release.Wait(CancellationToken.None);
            done.Set();
        };
        try
        {
            using var data = new MemoryStream(new byte[1024]);
            writer.Write("data", data, (DateTime?)null);
            writer.EndSolidBlock();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var watch = Stopwatch.StartNew();
            writer.Abort();
            writer.Abort();
            writer.EndSolidBlock();
            await writer.EndSolidBlockAsync(CancellationToken.None);
            writer.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            InvalidOutput(output);
        }
        finally
        {
            release.Set();
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            writer.Abort();
            writer.Dispose();
        }
    }

    [Fact]
    public void EncryptedBuffers_NeverSpillAndChargeFullSize()
    {
        using var pipeline = new SevenZipEncodePipeline(Options(password: "secret"));
        using var buffer = new SevenZipJobBuffer(pipeline.AllowSpill, 1);
        var before = Directory.GetFiles(Path.GetTempPath(), "sharpcompress-7z-*");
        buffer.Write(new byte[1024], 0, 1024);
        Assert.False(buffer.HasSpilled);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "sharpcompress-7z-*"));
        var properties = new LzmaEncoderProperties(false, 1 << 20);
        var size = SevenZipJobBuffer.MemoryLimit + 1024;
        Assert.True(
            SevenZipEncodePipeline.Estimate(size, CompressionType.LZMA2, properties, false)
                > SevenZipEncodePipeline.Estimate(size, CompressionType.LZMA2, properties)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_RejectsHeaderSearchAndNonzeroOrigin(bool asynchronous)
    {
        using var source = Source();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await SevenZipUpdate.OpenAsync(
                    source,
                    new ReaderOptions { LookForHeader = true },
                    CancellationToken.None
                )
            );
            source.Position = 1;
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await SevenZipUpdate.OpenAsync(source, cancellationToken: CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<NotSupportedException>(() =>
                SevenZipUpdate.Open(source, new ReaderOptions { LookForHeader = true })
            );
            source.Position = 1;
            Assert.Throws<NotSupportedException>(() => SevenZipUpdate.Open(source));
        }
    }

    [Fact]
    public void OversizedPackedChunk_UsesStoredFraming()
    {
        using var output = new MemoryStream();
        using var encoder = new Lzma2EncoderStream(
            output,
            new LzmaEncoderProperties(false, 1 << 20)
        );
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var packed = (Stream)
            typeof(Lzma2EncoderStream).GetField("_packed", flags)!.GetValue(encoder)!;
        packed.SetLength(65537);
        var input = new byte[131072];
        new Random(91).NextBytes(input);
        typeof(Lzma2EncoderStream).GetField("_chunkInput", flags)!.SetValue(encoder, input);
        Assert.True(
            (bool)
                typeof(Lzma2EncoderStream)
                    .GetMethod("CompleteChunk", flags)!
                    .Invoke(encoder, new object[] { input.Length })!
        );
        var framed = (Stream)
            typeof(Lzma2EncoderStream).GetField("_framed", flags)!.GetValue(encoder)!;
        framed.Position = 0;
        Assert.Equal(1, framed.ReadByte());
        Assert.Equal(255, framed.ReadByte());
        Assert.Equal(255, framed.ReadByte());
        var bytes = new byte[65536];
        framed.ReadExact(bytes, 0, bytes.Length);
        Assert.Equal(input.Take(65536), bytes);
        Assert.Equal(2, framed.ReadByte());
        Assert.Equal(255, framed.ReadByte());
        Assert.Equal(255, framed.ReadByte());
        framed.ReadExact(bytes, 0, bytes.Length);
        Assert.Equal(input.Skip(65536), bytes);
        encoder.Abort();
    }

    [Fact]
    public void LargeEncoderArrays_HaveExactLengths()
    {
        const int length = (1 << 20) + 1;
        var bytes = EncoderArrayPool.Rent(ArrayPool<byte>.Shared, length, 1);
        var words = EncoderArrayPool.Rent(ArrayPool<uint>.Shared, length, 4);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(length, words.Length);
        EncoderArrayPool.Return(ArrayPool<byte>.Shared, bytes, 1);
        EncoderArrayPool.Return(ArrayPool<uint>.Shared, words, 4);
        using var window = new InWindow();
        window.Create(1 << 20, 1, 65536);
        Assert.Equal((1 << 20) + 1 + 65536, window._bufferBase.Length);
        using var chain = new HashChain();
        using var tree = new BinTree();
        chain.Create(1 << 20, 0, 32, 0);
        tree.SetType(4);
        tree.Create(1 << 20, 0, 32, 0);
        foreach (var finder in new object[] { chain, tree })
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var son = (uint[])finder.GetType().GetField("_son", flags)!.GetValue(finder)!;
            var hash = (uint[])finder.GetType().GetField("_hash", flags)!.GetValue(finder)!;
            var hashSize = (uint)
                finder.GetType().GetField("_hashSizeSum", flags)!.GetValue(finder)!;
            Assert.Equal(finder is HashChain ? length : length * 2, son.Length);
            Assert.Equal((int)hashSize, hash.Length);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CleanRecompression_RoundTrips(bool asynchronous, bool parallel)
    {
        using var source = Source();
        using var update = SevenZipUpdate.Open(source);
        update.Delete(1);
        using var output = new MemoryStream();
        if (asynchronous)
        {
            await update.WriteToAsync(
                output,
                Options(parallel: parallel),
                cancellationToken: CancellationToken.None
            );
        }
        else
        {
            update.WriteTo(output, Options(parallel: parallel));
        }
        output.Position = 0;
        using var archive = SevenZipArchive.OpenArchive(output);
        using var decoded = archive.Entries.Single().OpenEntryStream();
        using var actual = new MemoryStream();
        decoded.CopyTo(actual);
        var expected = new byte[4096];
        new Random(381).NextBytes(expected);
        Assert.Equal(expected, actual.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IncompleteSource_HasFormatExceptionAndNoHeader(
        bool asynchronous,
        bool recompress
    )
    {
        using var source = Source(CompressionType.None);
        using var update = SevenZipUpdate.Open(source);
        if (recompress)
        {
            update.Delete(1);
        }
        source.SetLength(33);
        using var output = new MemoryStream();
        InvalidFormatException failure;
        if (asynchronous)
        {
            failure = await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await update.WriteToAsync(
                    output,
                    Options(),
                    cancellationToken: CancellationToken.None
                )
            );
        }
        else
        {
            failure = Assert.Throws<InvalidFormatException>(() =>
                update.WriteTo(output, Options())
            );
        }
        Assert.IsType<EndOfStreamException>(failure.InnerException);
        InvalidOutput(output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnsupportedCoderAndOverflow_HaveDiagnosticExceptions(
        bool asynchronous,
        bool overflow
    )
    {
        using var source = Source(CompressionType.None);
        using var update = SevenZipUpdate.Open(source);
        var database = (ArchiveDatabase)
            typeof(SevenZipUpdate)
                .GetField("database", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(update)!;
        if (overflow)
        {
            database._packSizes[0] = -1;
        }
        else
        {
            database._folders[0]._coders[0]._methodId = new CMethodId(0x123456);
            update.Delete(1);
        }
        using var output = new MemoryStream();
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
            {
                await update.WriteToAsync(
                    output,
                    Options(),
                    cancellationToken: CancellationToken.None
                );
            }
            else
            {
                update.WriteTo(output, Options());
            }
        });
        if (overflow)
        {
            Assert.IsType<OverflowException>(
                Assert.IsType<InvalidFormatException>(failure).InnerException
            );
        }
        else
        {
            Assert.Contains("123456", Assert.IsType<NotSupportedException>(failure).Message);
        }
        InvalidOutput(output);
    }

    [Fact]
    public void UpdateAbort_IsIdempotentAndSuppressesHeader()
    {
        using var source = Source();
        using var update = SevenZipUpdate.Open(source);
        using var output = new MemoryStream();
        update.WriteTo(
            output,
            Options(),
            _ =>
            {
                update.Abort();
                update.Abort();
            }
        );
        InvalidOutput(output);
        Assert.True(source.CanRead);
    }

    private sealed class FailedInput : MemoryStream
    {
        internal FailedInput()
            : base(new byte[32]) { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("input failed");

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => throw new IOException("input failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSolidFailure_DoesNotFinalize(bool asynchronous)
    {
        using var output = new MemoryStream();
        var options = Options(CompressionType.None);
        options.Solid = false;
        var writer = new SevenZipWriter(output, options);
        using var input = new FailedInput();
        if (asynchronous)
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await writer.WriteAsync("data", input, (DateTime?)null, CancellationToken.None)
            );
            await writer.DisposeAsync();
        }
        else
        {
            Assert.Throws<IOException>(() => writer.Write("data", input, (DateTime?)null));
            writer.Dispose();
        }
        InvalidOutput(output);
    }

    [Fact]
    public async Task AsyncLzmaEncoder_RejectsChunkMode()
    {
        using var output = new MemoryStream();
        using var encoder = new Encoder();
        encoder.StartLzma2(output, new byte[1 << 21], _ => false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await encoder.CodeAsync(Stream.Null, true, CancellationToken.None)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await encoder.CodeOneBlockAsync(CancellationToken.None)
        );
        Assert.Equal(0, output.Length);
    }
}
