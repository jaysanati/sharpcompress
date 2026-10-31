#pragma warning disable VSTHRD103 // Explicit useAsync branches preserve synchronous caller I/O; staging is local job storage.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.Filters;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Crypto;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.Common.SevenZip;

/// <summary>Sequential file accounting and filtering, independent block jobs, sequential AES commit.</summary>
internal sealed class SevenZipParallelFolder : IDisposable
{
    private readonly SevenZipEncodePipeline pipeline;
    private readonly Stream output;
    private readonly SevenZipAesEncryption? encryption;
    private readonly Action<PackedStream> complete;
    private readonly SevenZipFilter filterKind;
    private readonly Filter? filter;
    private readonly byte[] buffer = new byte[65536 + 8];
    private readonly long blockSize;
    private readonly byte[] coderProperties;
    private readonly List<ulong> sizes = new();
    private readonly List<uint?> crcs = new();
    private SevenZipJobBuffer? pending;
    private int tail;
    private long start;
    private Crc32Stream? packedCrc;
    private AesEncoderStream? aes;
    private byte[]? aesProperties;
    private bool disposed;
    internal CompressionType Compression { get; }
    internal LzmaEncoderProperties Properties { get; }
    internal long Size { get; private set; }
    internal int FileCount => sizes.Count;

    internal SevenZipParallelFolder(
        SevenZipEncodePipeline pipeline,
        Stream output,
        SevenZipSolidBlockOptions options,
        SevenZipAesEncryption? encryption,
        Action<PackedStream> complete
    )
    {
        this.pipeline = pipeline;
        this.output = output;
        this.encryption = encryption;
        this.complete = complete;
        Compression = options.CompressionType!.Value;
        Properties =
            options.LzmaProperties
            ?? new LzmaEncoderProperties(eos: Compression == CompressionType.LZMA);
        if (options.ExpectedSize is long size)
        {
            Properties = SevenZipStreamsCompressor.ReduceDictionary(size, Properties);
        }
        blockSize =
            Compression == CompressionType.LZMA
                ? long.MaxValue
                : Math.Min(256L << 20, Math.Max(1L << 20, 4L * Properties.DictionarySize));
        if (
            Compression == CompressionType.LZMA2
            && options.ExpectedSize is long expectedSize
            && expectedSize > 0
            && 1 + (expectedSize - 1) / blockSize < pipeline.MaxThreads
        )
        {
            // Divide before rounding to avoid overflow for very large expected sizes.
            var perThread = 1 + (expectedSize - 1) / pipeline.MaxThreads;
            var rounded = ((perThread - 1) / (1L << 20) + 1) * (1L << 20);
            blockSize = Math.Min(
                blockSize,
                Math.Max(Properties.DictionarySize, Math.Max(1L << 20, rounded))
            );
        }
        if (Compression == CompressionType.LZMA2)
        {
            coderProperties = new[]
            {
                Lzma2EncoderStream.EncodeDictionarySize(Properties.DictionarySize),
            };
        }
        else if (Compression == CompressionType.LZMA)
        {
            using var encoder = new Encoder();
            encoder.SetCoderProperties(Properties.PropIDs, Properties.Properties);
            coderProperties = new byte[5];
            encoder.WriteCoderProperties(coderProperties.AsSpan());
        }
        else
        {
            coderProperties = Array.Empty<byte>();
        }
        filterKind = options.Filter;
        filter = filterKind switch
        {
            SevenZipFilter.X86 => new BCJFilter(true, Stream.Null),
            SevenZipFilter.Arm64 => new BCJFilterARM64(true, Stream.Null),
            _ => null,
        };
    }

    internal async ValueTask<long> WriteFileAsync(
        Stream source,
        bool useAsync,
        CancellationToken token
    )
    {
        pipeline.Observe(token);
        var seed = Crc32Stream.DEFAULT_SEED;
        long size = 0;
        while (true)
        {
            pipeline.Check();
            var read = useAsync
                ? await source.ReadAsync(buffer, tail, 65536, pipeline.Token).ConfigureAwait(false)
                : source.Read(buffer, tail, 65536);
            if (read == 0)
            {
                break;
            }
            seed = ~Crc32Stream.Compute(
                Crc32Stream.DEFAULT_POLYNOMIAL,
                seed,
                buffer.AsSpan(tail, read)
            );
            size = checked(size + read);
            var count = tail + read;
            var transformed = filter?.TransformBlock(buffer, count) ?? count;
            await StageAsync(buffer, transformed, useAsync).ConfigureAwait(false);
            tail = count - transformed;
            Buffer.BlockCopy(buffer, transformed, buffer, 0, tail);
        }
        if (size > 0)
        {
            Size = checked(Size + size);
            sizes.Add((ulong)size);
            crcs.Add(~seed);
        }
        return size;
    }

    internal void RecordEmptyStream()
    {
        sizes.Add(0);
        crcs.Add(0);
    }

    private async ValueTask StageAsync(byte[] bytes, int count, bool useAsync)
    {
        var offset = 0;
        while (offset < count)
        {
            pending ??= new SevenZipJobBuffer();
            var length = (int)Math.Min(count - offset, blockSize - pending.Length);
            pending.Write(bytes, offset, length);
            offset += length;
            if (pending.Length == blockSize)
            {
                await SubmitPendingAsync(useAsync).ConfigureAwait(false);
            }
        }
    }

    private ValueTask SubmitPendingAsync(bool useAsync)
    {
        var input = pending!;
        pending = null;
        return pipeline.SubmitAsync(this, input, useAsync);
    }

    internal async ValueTask FinishAsync(bool useAsync)
    {
        pipeline.Check();
        await StageAsync(buffer, tail, useAsync).ConfigureAwait(false);
        tail = 0;
        if (pending is not null)
        {
            await SubmitPendingAsync(useAsync).ConfigureAwait(false);
        }
        if (FileCount > 0)
        {
            // A stream-bearing zero-length update entry still needs a valid codec stream.
            if (Size == 0)
            {
                await pipeline
                    .SubmitAsync(this, new SevenZipJobBuffer(), useAsync)
                    .ConfigureAwait(false);
            }
            pipeline.End(this);
        }
        else
        {
            Dispose();
        }
    }

    private Stream Destination
    {
        get
        {
            if (packedCrc is null)
            {
                start = output.Position;
                packedCrc = new Crc32Stream(output);
                aes = encryption?.CreateEncoder(packedCrc, out aesProperties);
            }
            return aes ?? (Stream)packedCrc;
        }
    }

    internal async ValueTask CommitAsync(
        SevenZipJobBuffer data,
        bool useAsync,
        CancellationToken token
    )
    {
        if (useAsync)
        {
            await data.CopyToAsync(Destination, 65536, token).ConfigureAwait(false);
        }
        else
        {
            data.CopyTo(Destination, 65536);
        }
    }

    internal async ValueTask CommitEndAsync(bool useAsync, CancellationToken token)
    {
        var destination = Destination;
        if (Compression == CompressionType.LZMA2)
        {
            if (useAsync)
            {
                await destination.WriteAsync(new byte[] { 0 }, 0, 1, token).ConfigureAwait(false);
            }
            else
            {
                destination.WriteByte(0);
            }
        }
        if (aes is not null)
        {
            if (useAsync)
            {
                await aes.FinishAsync(token).ConfigureAwait(false);
            }
            else
            {
                aes.Finish();
            }
        }
        var method =
            Compression == CompressionType.LZMA2 ? CMethodId.K_LZMA2
            : Compression == CompressionType.LZMA ? CMethodId.K_LZMA
            : CMethodId.K_COPY;
        var packed = SevenZipStreamsCompressor.BuildPackedStream(
            method,
            coderProperties,
            (ulong)(output.Position - start),
            (ulong)Size,
            0,
            packedCrc!.Crc,
            aesProperties,
            aes?.InputLength
        );
        var folder = packed.Folder;
        folder._unpackCrc = null;
        if (filter is not null)
        {
            var index = folder._coders.Count;
            folder._coders.Add(
                new CCoderInfo
                {
                    _methodId = new CMethodId(
                        filterKind == SevenZipFilter.X86 ? 0x03030103UL : 0x0AUL
                    ),
                    _numInStreams = 1,
                    _numOutStreams = 1,
                    _props = Array.Empty<byte>(),
                }
            );
            folder._bindPairs.Add(new CBindPair { _inIndex = index, _outIndex = index - 1 });
            folder._unpackSizes.Add(Size);
        }
        complete(
            new PackedStream
            {
                Folder = folder,
                Sizes = packed.Sizes,
                CRCs = packed.CRCs,
                SubStreamSizes = sizes.ToArray(),
                SubStreamCRCs = crcs.ToArray(),
            }
        );
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        pending?.Dispose();
        pending = null;
        aes?.Dispose();
        packedCrc?.Dispose();
        filter?.Dispose();
    }
}
