using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.Filters;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.Compressors.PPMd;
using SharpCompress.Compressors.ZStandard;

namespace SharpCompress.Compressors.LZMA;

internal static partial class DecoderRegistry
{
    private const uint K_COPY = 0x0;
    private const uint K_DELTA = 0x3;
    private const uint K_LZMA2 = 0x21;
    private const uint K_LZMA = 0x030101;
    private const uint K_PPMD = 0x030401;
    private const uint K_BCJ = 0x03030103;
    private const uint K_BCJ2 = 0x0303011B;
    private const uint K_PPC = 0x03030205;
    private const uint K_IA64 = 0x03030401;
    private const uint K_ARM = 0x03030501;
    private const uint K_ARMT = 0x03030701;
    private const uint K_SPARC = 0x03030805;
    private const uint K_ARM64 = 0x0A;
    private const uint K_RISCV = 0x0B;
    private const uint K_DEFLATE = 0x040108;
    private const uint K_B_ZIP2 = 0x040202;
    private const uint K_ZSTD = 0x4F71101;

    internal static void ValidateProperties(
        CMethodId id,
        byte[]? info,
        long? maxDecoderMemory = null
    )
    {
        var length = id._id switch
        {
            K_LZMA => 5,
            K_LZMA2 or K_DELTA => 1,
            K_PPMD => 5,
            _ => -1,
        };
        if (length >= 0 && (info is null || info.Length != length))
        {
            throw new InvalidFormatException("Invalid 7z coder property length.");
        }
        long memory = 0;
        if (id._id == K_PPMD)
        {
            memory = new PpmdProperties(info!).AllocatorSize;
        }
        else if (id._id == K_LZMA)
        {
            memory = BinaryPrimitives.ReadUInt32LittleEndian(info!.AsSpan(1));
        }
        else if (id._id == K_LZMA2 && info![0] <= 40)
        {
            memory = info[0] == 40 ? uint.MaxValue : (2L | (info[0] & 1)) << ((info[0] >> 1) + 11);
        }
        if (maxDecoderMemory is long maximum && memory > maximum)
        {
            throw new NotSupportedException(
                "7z decoder memory exceeds ReaderOptions.MaxDecoderMemory."
            );
        }
    }

    internal static Stream CreateDecoderStream(
        CMethodId id,
        Stream[] inStreams,
        byte[]? info,
        IPasswordProvider pass,
        long limit
    )
    {
        ValidateProperties(id, info);
        switch (id._id)
        {
            case K_COPY:
                if (info != null)
                {
                    throw new NotSupportedException(
                        $"Unsupported 7z coder 0x{id._id:X} or coder properties."
                    );
                }
                return inStreams.Single();
            case K_DELTA:
                return new DeltaFilter(false, inStreams.Single(), info.NotNull());
            case K_LZMA:
            case K_LZMA2:
                return LzmaStream.Create(
                    info.NotNull(),
                    inStreams.Single(),
                    -1,
                    limit,
                    null,
                    id._id == K_LZMA2
                );
            case CMethodId.K_AES_ID:
                return new AesDecoderStream(inStreams.Single(), info.NotNull(), pass, limit);
            case K_BCJ:
                return new BCJFilter(false, inStreams.Single());
            case K_BCJ2:
                return new Bcj2DecoderStream(inStreams);
            case K_PPC:
                return new BCJFilterPPC(false, inStreams.Single());
            case K_IA64:
                return new BCJFilterIA64(false, inStreams.Single());
            case K_ARM:
                return new BCJFilterARM(false, inStreams.Single());
            case K_ARMT:
                return new BCJFilterARMT(false, inStreams.Single());
            case K_SPARC:
                return new BCJFilterSPARC(false, inStreams.Single());
            case K_ARM64:
                return new BCJFilterARM64(false, inStreams.Single());
            case K_RISCV:
                return new BCJFilterRISCV(false, inStreams.Single());
            case K_B_ZIP2:
                return BZip2Stream.Create(inStreams.Single(), CompressionMode.Decompress, true);
            case K_PPMD:
                return PpmdStream.Create(
                    new PpmdProperties(info.NotNull()),
                    inStreams.Single(),
                    false
                );
            case K_DEFLATE:
                return new DeflateStream(inStreams.Single(), CompressionMode.Decompress);
            case K_ZSTD:
                return new DecompressionStream(inStreams.Single());
            default:
                throw new NotSupportedException(
                    $"Unsupported 7z coder 0x{id._id:X} or coder properties."
                );
        }
    }
}
