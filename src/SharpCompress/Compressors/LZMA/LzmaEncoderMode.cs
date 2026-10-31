namespace SharpCompress.Compressors.LZMA;

/// <summary>Parsing strategies for LZMA and LZMA2 compression.</summary>
public enum LzmaEncoderMode
{
    /// <summary>Fast parsing with one-position look-ahead.</summary>
    Fast = 0,

    /// <summary>Price-based optimum parsing (the default).</summary>
    Normal = 1,
}
