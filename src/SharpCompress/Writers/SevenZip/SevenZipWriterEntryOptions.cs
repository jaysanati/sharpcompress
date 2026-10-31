using System;

namespace SharpCompress.Writers.SevenZip;

/// <summary>
/// Metadata for a 7z entry. A value type keeps null arguments to the existing
/// DateTime? writer overloads unambiguous.
/// </summary>
public struct SevenZipWriterEntryOptions
{
    /// <summary>Modification time in UTC, or null to omit it.</summary>
    public DateTime? ModificationTime { get; set; }

    /// <summary>Creation time in UTC, or null to omit it.</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>Last access time in UTC, or null to omit it.</summary>
    public DateTime? LastAccessTime { get; set; }

    /// <summary>
    /// Raw 7z attributes, or null to omit them. The low 16 bits contain Windows
    /// attributes; when bit 0x8000 is set, the high 16 bits contain the Unix mode.
    /// Directories additionally receive FILE_ATTRIBUTE_DIRECTORY (0x10).
    /// </summary>
    public uint? Attributes { get; set; }
}
