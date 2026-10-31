namespace SharpCompress.Compressors.LZMA;

/// <summary>Match-finding strategies for LZMA and LZMA2 compression.</summary>
public enum LzmaMatchFinder
{
    /// <summary>Binary tree with a two-byte hash.</summary>
    BT2,

    /// <summary>Binary tree with two-, three- and four-byte hashes (the default).</summary>
    BT4,

    /// <summary>Hash chain with two-, three- and four-byte hashes.</summary>
    HC4,
}
