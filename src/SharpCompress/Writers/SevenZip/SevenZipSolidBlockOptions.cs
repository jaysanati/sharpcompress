using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;

namespace SharpCompress.Writers.SevenZip;

/// <summary>Branch filter applied to a solid block before compression.</summary>
public enum SevenZipFilter
{
    None,
    X86,
    Arm64,
}

/// <summary>Compression settings for a caller-defined solid block.</summary>
public sealed class SevenZipSolidBlockOptions
{
    /// <summary>Codec for this block; null uses the writer's codec.</summary>
    public CompressionType? CompressionType { get; set; }

    /// <summary>Encoder properties; null uses the writer's properties.</summary>
    public LzmaEncoderProperties? LzmaProperties { get; set; }

    /// <summary>Branch filter, disabled by default.</summary>
    public SevenZipFilter Filter { get; set; }

    /// <summary>Expected input size used to reduce the dictionary; null retains its configured size.</summary>
    public long? ExpectedSize { get; set; }
}
