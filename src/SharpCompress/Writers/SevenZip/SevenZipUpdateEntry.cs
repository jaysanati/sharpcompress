using System;
using SharpCompress.Common.SevenZip;

namespace SharpCompress.Writers.SevenZip;

/// <summary>A source entry in a single-use 7z update. Mutate it through SevenZipUpdate.</summary>
public sealed class SevenZipUpdateEntry
{
    internal SevenZipUpdateEntry(int index, CFileItem file, int folderIndex)
    {
        Index = index;
        Name = file.Name;
        IsDirectory = file.IsDir;
        IsAnti = file.IsAnti;
        Size = file.Size;
        Crc = file.Crc;
        FolderIndex = file.HasStream ? folderIndex : -1;
        Metadata = new SevenZipWriterEntryOptions
        {
            CreationTime = file.CTime,
            LastAccessTime = file.ATime,
            ModificationTime = file.MTime,
            Attributes = file.ExtendedAttrib ?? file.Attrib,
        };
    }

    /// <summary>Zero-based index in the source archive.</summary>
    public int Index { get; }

    /// <summary>Current archive name, including any rename.</summary>
    public string Name { get; internal set; }

    /// <summary>Whether this entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Whether this entry is a 7z anti-item.</summary>
    public bool IsAnti { get; }

    /// <summary>Uncompressed size.</summary>
    public long Size { get; }

    /// <summary>Source CRC, when defined.</summary>
    public uint? Crc { get; }

    /// <summary>Creation time, when defined.</summary>
    public DateTime? CreationTime => Metadata.CreationTime;

    /// <summary>Access time, when defined.</summary>
    public DateTime? LastAccessTime => Metadata.LastAccessTime;

    /// <summary>Modification time, when defined.</summary>
    public DateTime? ModificationTime => Metadata.ModificationTime;

    /// <summary>Raw attributes, including Unix mode bits, when defined.</summary>
    public uint? Attributes => Metadata.Attributes;

    /// <summary>Source folder index, or -1 for entries without streams.</summary>
    public int FolderIndex { get; }

    /// <summary>Whether the entry will be omitted.</summary>
    public bool IsDeleted { get; internal set; }

    internal SevenZipWriterEntryOptions Metadata { get; set; }

    internal SevenZipWriteEntry ToWriteEntry() =>
        new()
        {
            Name = Name,
            IsDirectory = IsDirectory,
            IsAnti = IsAnti,
            IsEmpty = FolderIndex < 0,
            CreationTime = CreationTime,
            LastAccessTime = LastAccessTime,
            ModificationTime = ModificationTime,
            Attributes = Attributes,
        };
}
