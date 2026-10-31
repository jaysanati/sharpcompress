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

/// <summary>A streaming folder encoder. Only a bounded filter tail survives between files.</summary>
internal sealed class SevenZipSolidEncoder : IDisposable, IAsyncDisposable
{
    private readonly Stream output;
    private readonly long start;
    private readonly Crc32Stream packedCrc;
    private readonly AesEncoderStream? aes;
    private readonly byte[]? aesProperties;
    private readonly Stream codec;
    private readonly CMethodId method;
    private readonly byte[] properties;
    private readonly Filter? filter;
    private readonly SevenZipFilter filterKind;
    private readonly byte[] buffer = new byte[65536 + 8];
    private readonly List<ulong> sizes = [];
    private readonly List<uint?> crcs = [];
    private int tail;
    private bool codecClosed;
    public long Size { get; private set; }
    public int FileCount => sizes.Count;

    public SevenZipSolidEncoder(
        Stream output,
        CompressionType compression,
        LzmaEncoderProperties? properties,
        SevenZipFilter filterKind,
        long? expectedSize,
        SevenZipAesEncryption? encryption
    )
    {
        this.output = output;
        start = output.Position;
        packedCrc = new Crc32Stream(output);
        aes = encryption?.CreateEncoder(packedCrc, out aesProperties);
        Stream destination = aes ?? (Stream)packedCrc;
        properties ??= new LzmaEncoderProperties(eos: compression == CompressionType.LZMA);
        if (expectedSize is long expected)
        {
            properties = SevenZipStreamsCompressor.ReduceDictionary(expected, properties);
        }
        try
        {
            switch (compression)
            {
                case CompressionType.LZMA:
                    var lzma = LzmaStream.Create(properties, false, destination);
                    codec = lzma;
                    this.properties = lzma.Properties;
                    method = CMethodId.K_LZMA;
                    break;
                case CompressionType.LZMA2:
                    var lzma2 = new Lzma2EncoderStream(destination, properties);
                    codec = lzma2;
                    this.properties = lzma2.Properties;
                    method = CMethodId.K_LZMA2;
                    break;
                default:
                    codec = destination;
                    this.properties = [];
                    method = CMethodId.K_COPY;
                    break;
            }
        }
        catch
        {
            aes?.Dispose();
            packedCrc.Dispose();
            throw;
        }
        this.filterKind = filterKind;
        // Reuse the existing instruction converters; the writer owns chunk staging and tail retention.
        filter = filterKind switch
        {
            SevenZipFilter.X86 => new BCJFilter(true, Stream.Null),
            SevenZipFilter.Arm64 => new BCJFilterARM64(true, Stream.Null),
            _ => null,
        };
    }

    public long WriteFile(Stream source)
    {
        var seed = Crc32Stream.DEFAULT_SEED;
        long size = 0;
        int read;
        while ((read = source.Read(buffer, tail, 65536)) > 0)
        {
            seed = ~Crc32Stream.Compute(
                Crc32Stream.DEFAULT_POLYNOMIAL,
                seed,
                buffer.AsSpan(tail, read)
            );
            size += read;
            var count = tail + read;
            var transformed = filter?.TransformBlock(buffer, count) ?? count;
            codec.Write(buffer, 0, transformed);
            tail = count - transformed;
            Buffer.BlockCopy(buffer, transformed, buffer, 0, tail);
        }
        RecordFile(size, ~seed);
        return size;
    }

    public async ValueTask<long> WriteFileAsync(Stream source, CancellationToken cancellationToken)
    {
        var seed = Crc32Stream.DEFAULT_SEED;
        long size = 0;
        int read;
        while (
            (
                read = await source
                    .ReadAsync(buffer, tail, 65536, cancellationToken)
                    .ConfigureAwait(false)
            ) > 0
        )
        {
            seed = ~Crc32Stream.Compute(
                Crc32Stream.DEFAULT_POLYNOMIAL,
                seed,
                buffer.AsSpan(tail, read)
            );
            size += read;
            var count = tail + read;
            var transformed = filter?.TransformBlock(buffer, count) ?? count;
            await codec.WriteAsync(buffer, 0, transformed, cancellationToken).ConfigureAwait(false);
            tail = count - transformed;
            Buffer.BlockCopy(buffer, transformed, buffer, 0, tail);
        }
        RecordFile(size, ~seed);
        return size;
    }

    // Updates preserve stream-bearing zero-length entries as real substreams.
    internal void RecordEmptyStream()
    {
        sizes.Add(0);
        crcs.Add(0);
    }

    private void RecordFile(long size, uint crc)
    {
        if (size == 0)
        {
            return;
        }
        Size = checked(Size + size);
        sizes.Add((ulong)size);
        crcs.Add(crc);
    }

    public PackedStream Finish()
    {
        try
        {
            codec.Write(buffer, 0, tail);
            if (method != CMethodId.K_COPY)
            {
                codecClosed = true;
                codec.Dispose();
            }
            aes?.Finish();
            return BuildResult();
        }
        finally
        {
            Dispose();
        }
    }

    public async ValueTask<PackedStream> FinishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await codec.WriteAsync(buffer, 0, tail, cancellationToken).ConfigureAwait(false);
            if (method != CMethodId.K_COPY)
            {
                await CloseCodecAsync().ConfigureAwait(false);
            }
            if (aes is not null)
            {
                await aes.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
            return BuildResult();
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CloseCodecAsync()
    {
        if (codecClosed)
        {
            return;
        }
        codecClosed = true;
        // The concrete encoder methods also exist on targets whose Stream has no DisposeAsync.
        if (codec is Lzma2EncoderStream lzma2)
        {
            await lzma2.DisposeAsync().ConfigureAwait(false);
        }
        else if (codec is LzmaStream lzma)
        {
            await lzma.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseCodecAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseResources();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!codecClosed)
            {
                codecClosed = true;
                if (method != CMethodId.K_COPY)
                {
                    codec.Dispose();
                }
            }
        }
        finally
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        // These wrappers own only CRC/filter state and AES buffers; disposal performs no I/O.
        aes?.Dispose();
        packedCrc.Dispose();
        filter?.Dispose();
    }

    private PackedStream BuildResult()
    {
        var packed = SevenZipStreamsCompressor.BuildPackedStream(
            method,
            properties,
            (ulong)(output.Position - start),
            (ulong)Size,
            0,
            packedCrc.Crc,
            aesProperties,
            aes?.InputLength
        );
        var folder = packed.Folder;
        folder._unpackCrc = null;
        if (filter is not null)
        {
            // Decode order is AES -> codec -> filter, with packed input 0 and one final output.
            var index = folder._coders.Count;
            folder._coders.Add(
                new CCoderInfo
                {
                    _methodId = new CMethodId(
                        filterKind == SevenZipFilter.X86 ? 0x03030103UL : 0x0AUL
                    ),
                    _numInStreams = 1,
                    _numOutStreams = 1,
                    _props = [],
                }
            );
            folder._bindPairs.Add(new CBindPair { _inIndex = index, _outIndex = index - 1 });
            folder._unpackSizes.Add(Size);
        }
        if (FileCount == 0)
        {
            output.Position = start;
            output.SetLength(start);
        }
        return new PackedStream
        {
            Folder = folder,
            Sizes = packed.Sizes,
            CRCs = packed.CRCs,
            SubStreamSizes = sizes.ToArray(),
            SubStreamCRCs = crcs.ToArray(),
        };
    }
}
