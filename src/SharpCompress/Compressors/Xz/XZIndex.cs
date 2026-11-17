using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Xz;

[CLSCompliant(false)]
public partial class XZIndex
{
    private readonly BinaryReader _reader;
    public long StreamStartPosition { get; private set; }
    public ulong NumberOfRecords { get; private set; }
    public List<XZIndexRecord> Records { get; } = new();

    private readonly bool _indexMarkerAlreadyVerified;

    public XZIndex(BinaryReader reader, bool indexMarkerAlreadyVerified)
    {
        _reader = reader;
        _indexMarkerAlreadyVerified = indexMarkerAlreadyVerified;
        StreamStartPosition = reader.BaseStream.Position;
        if (indexMarkerAlreadyVerified)
        {
            StreamStartPosition--;
        }
    }

    public static XZIndex FromStream(Stream stream, bool indexMarkerAlreadyVerified)
    {
        var index = new XZIndex(
            new BinaryReader(stream, Encoding.UTF8, true),
            indexMarkerAlreadyVerified
        );
        index.Process();
        return index;
    }

    public void Process()
    {
        if (!_indexMarkerAlreadyVerified)
        {
            VerifyIndexMarker();
        }

        NumberOfRecords = _reader.ReadXZInteger();
        for (ulong i = 0; i < NumberOfRecords; i++)
        {
            Records.Add(XZIndexRecord.FromBinaryReader(_reader));
        }
        SkipPadding();
        VerifyCrc32();
    }

    private void VerifyIndexMarker()
    {
        var marker = _reader.ReadByte();
        if (marker != 0)
        {
            throw new InvalidFormatException("Not an index block");
        }
    }

    private void SkipPadding()
    {
        var bytes = (int)(_reader.BaseStream.Position - StreamStartPosition) % 4;
        if (bytes > 0)
        {
            var paddingBytes = _reader.ReadBytes(4 - bytes);
            if (paddingBytes.Any(b => b != 0))
            {
                throw new InvalidFormatException("Padding bytes were non-null");
            }
        }
    }

    private void VerifyCrc32()
    {
        var expected = ComputeCrc32();
        var crc = _reader.ReadLittleEndianUInt32();
        if (crc != expected)
        {
            throw new InvalidFormatException("XZ index CRC32 mismatch.");
        }
    }

    private uint ComputeCrc32()
    {
        // ReadXZInteger accepts only canonical encodings, so re-encoding records
        // hashes exactly the index bytes without retaining a second copy of them.
        var crc = Crc32.Update(Crc32.DefaultSeed, new byte[] { 0 });
        var size = 1;
        AppendInteger(NumberOfRecords, ref crc, ref size);
        foreach (var record in Records)
        {
            AppendInteger(record.UnpaddedSize, ref crc, ref size);
            AppendInteger(record.UncompressedSize, ref crc, ref size);
        }
        Span<byte> padding = stackalloc byte[3];
        padding.Clear();
        crc = Crc32.Update(crc, padding.Slice(0, (4 - size % 4) % 4));
        return ~crc;
    }

    private static void AppendInteger(ulong value, ref uint crc, ref int size)
    {
        Span<byte> bytes = stackalloc byte[9];
        var count = 0;
        do
        {
            bytes[count] = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                bytes[count] |= 0x80;
            }
            count++;
        } while (value != 0);
        crc = Crc32.Update(crc, bytes.Slice(0, count));
        // Only alignment is needed; avoid accumulating an index-sized integer.
        size = (size + count) % 4;
    }
}
