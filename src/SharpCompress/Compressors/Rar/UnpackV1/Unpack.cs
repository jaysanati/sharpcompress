#nullable disable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.PPMd.H;
using SharpCompress.Compressors.Rar.UnpackV1.Decode;
using SharpCompress.Compressors.Rar.UnpackV1.PPM;
using SharpCompress.Compressors.Rar.VM;

namespace SharpCompress.Compressors.Rar.UnpackV1;

internal sealed partial class Unpack : BitInput, IRarUnpack
{
    private readonly BitInput Inp;
    private bool disposed;

    private readonly long? maxDecoderMemory;
    private bool firstWindowDone;

    public Unpack(long? maxDecoderMemory = null)
    {
        Inp = this;
        this.maxDecoderMemory = maxDecoderMemory;
        ppm.MaxDecoderMemory = maxDecoderMemory;
    }

    private void ValidateDistance(int distance)
    {
        if (
            distance <= 0
            || distance > PackDef.MAXWINSIZE
            || (!firstWindowDone && distance > unpPtr)
        )
        {
            throw new InvalidFormatException("RAR match distance exceeds available history.");
        }
    }

    public override void Dispose()
    {
        if (!disposed)
        {
            base.Dispose();
            ReleaseWindow();
            rarVM.Dispose();
            ppm.Dispose();
            disposed = true;
        }
    }

    private void ReleaseWindow()
    {
        if (window is not null)
        {
            ArrayPool<byte>.Shared.Return(window);
            window = null;
        }
    }

    public bool FileExtracted { get; private set; }

    public long DestSize
    {
        get => destUnpSize;
        set
        {
            destUnpSize = value;
            FileExtracted = false;
        }
    }

    public bool Suspended
    {
        get => suspended;
        set => suspended = value;
    }

    public int ReadChar()
    {
        if (inAddr >= readTop)
        {
            unpReadBuf();
        }

        if (inAddr >= readTop)
        {
            throw new InvalidFormatException("Truncated RAR PPM data.");
        }
        return (InBuf[inAddr++] & 0xff);
    }

    public int PpmEscChar { get; set; }

    private readonly ModelPpm ppm = new();

    private readonly RarVM rarVM = new();

    // Filters code, one entry per filter
    private readonly List<UnpackFilter> filters = new();

    // Filters stack, several entrances of same filter are possible
    private readonly List<UnpackFilter> prgStack = new();

    // lengths of preceding blocks, one length per filter. Used to reduce size
    // required to write block length if lengths are repeating
    private readonly List<int> oldFilterLengths = new();

    private int lastFilter;

    private bool tablesRead;

    private readonly byte[] unpOldTable = new byte[PackDef.HUFF_TABLE_SIZE];

    private BlockTypes unpBlockType;

    private long writtenFileSize;

    private bool ppmError;

    private int prevLowDist;

    private int lowDistRepCount;

    private static readonly int[] DBitLengthCounts =
    {
        4,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        2,
        14,
        0,
        12,
    };

    private FileHeader fileHeader;

    private void Init()
    {
        if (maxDecoderMemory is long maximum && PackDef.MAXWINSIZE > maximum)
        {
            throw new NotSupportedException("RAR window exceeds ReaderOptions.MaxDecoderMemory.");
        }
        if (this.window is null)
        {
            this.window = ArrayPool<byte>.Shared.Rent(PackDef.MAXWINSIZE);
            Array.Clear(window, 0, PackDef.MAXWINSIZE);
        }

        inAddr = 0;
        UnpInitData(false);
    }

    public void DoUnpack(FileHeader fileHeader, Stream readStream, Stream writeStream)
    {
        destUnpSize = fileHeader.UncompressedSize;
        oldWrittenSize = 0;
        this.fileHeader = fileHeader;
        this.readStream = readStream;
        this.writeStream = writeStream;
        if (!fileHeader.IsSolid || window is null)
        {
            firstWindowDone = false;
            if (fileHeader.IsStored)
            {
                ReleaseWindow();
            }
            else
            {
                Init();
            }
        }

        suspended = false;
        DoUnpack();
    }

    public void DoUnpack()
    {
        if (fileHeader.CompressionMethod == 0)
        {
            UnstoreFile();
            return;
        }

        switch (fileHeader.CompressionAlgorithm)
        {
            case 15: // rar 1.5 compression
                unpack15(fileHeader.IsSolid);
                break;

            case 20: // rar 2.x compression
            case 26: // files larger than 2GB
                unpack20(fileHeader.IsSolid);
                break;

            case 29: // rar 3.x compression
            case 36: // alternative hash
                Unpack29(fileHeader.IsSolid);
                break;

            case 50: // rar 5.x compression
                Unpack5(fileHeader.IsSolid);
                break;

            default:
                throw new InvalidFormatException(
                    "unknown rar compression version " + fileHeader.CompressionAlgorithm
                );
        }
    }

    private void UnstoreFile()
    {
        var rented = ArrayPool<byte>.Shared.Rent(0x10000);
        try
        {
            var buffer = rented.AsSpan(0, 0x10000);
            do
            {
                var code = readStream.Read(
                    buffer.Slice(0, (int)Math.Min(buffer.Length, destUnpSize))
                );
                if (code == 0 || code == -1)
                {
                    break;
                }

                code = code < destUnpSize ? code : (int)destUnpSize;
                writeStream.Write(buffer.Slice(0, code));
                destUnpSize -= code;
            } while (!suspended && destUnpSize > 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Unpack29(bool solid)
    {
        Span<int> DDecode = stackalloc int[PackDef.DC];
        Span<byte> DBits = stackalloc byte[PackDef.DC];

        int Bits;

        if (DDecode[1] == 0)
        {
            int Dist = 0,
                BitLength = 0,
                Slot = 0;
            for (var I = 0; I < DBitLengthCounts.Length; I++, BitLength++)
            {
                var count = DBitLengthCounts[I];
                for (var J = 0; J < count; J++, Slot++, Dist += (1 << BitLength))
                {
                    DDecode[Slot] = Dist;
                    DBits[Slot] = (byte)BitLength;
                }
            }
        }

        FileExtracted = true;

        if (!suspended)
        {
            UnpInitData(solid);
            if (!unpReadBuf())
            {
                return;
            }

            if ((!solid || !tablesRead) && !ReadTables())
            {
                ppmError = unpBlockType == BlockTypes.BLOCK_PPM;
                return;
            }
        }

        if (ppmError)
        {
            return;
        }

        while (true)
        {
            firstWindowDone |= unpPtr >= PackDef.MAXWINSIZE;
            unpPtr &= PackDef.MAXWINMASK;

            if (inAddr > readBorder)
            {
                if (!unpReadBuf())
                {
                    break;
                }
            }

            // System.out.println(((wrPtr - unpPtr) &
            // Compress.MAXWINMASK)+":"+wrPtr+":"+unpPtr);
            if (((wrPtr - unpPtr) & PackDef.MAXWINMASK) < 288 && wrPtr != unpPtr)
            {
                UnpWriteBuf();
                if (destUnpSize < 0)
                {
                    return;
                }

                if (suspended)
                {
                    FileExtracted = false;
                    return;
                }
            }

            if (unpBlockType == BlockTypes.BLOCK_PPM)
            {
                var Ch = ppm.DecodeChar();
                if (Ch == -1)
                {
                    ppmError = true;
                    break;
                }

                if (Ch == PpmEscChar)
                {
                    var NextCh = ppm.DecodeChar();
                    if (NextCh == 0)
                    {
                        if (!ReadTables())
                        {
                            ppmError = unpBlockType == BlockTypes.BLOCK_PPM;
                            break;
                        }

                        continue;
                    }

                    if (NextCh == 2 || NextCh == -1)
                    {
                        break;
                    }

                    if (NextCh == 3)
                    {
                        if (!ReadVMCodePPM())
                        {
                            break;
                        }

                        continue;
                    }

                    if (NextCh == 4)
                    {
                        int Distance = 0,
                            Length = 0;
                        var failed = false;
                        for (var I = 0; I < 4 && !failed; I++)
                        {
                            var ch = ppm.DecodeChar();
                            if (ch == -1)
                            {
                                failed = true;
                            }
                            else
                            {
                                if (I == 3)
                                {
                                    // Bug fixed
                                    Length = ch & 0xff;
                                }
                                else
                                {
                                    // Bug fixed
                                    Distance = (Distance << 8) + (ch & 0xff);
                                }
                            }
                        }

                        if (failed)
                        {
                            break;
                        }

                        CopyString(Length + 32, Distance + 2);
                        continue;
                    }

                    if (NextCh == 5)
                    {
                        var Length = ppm.DecodeChar();
                        if (Length == -1)
                        {
                            break;
                        }

                        CopyString(Length + 4, 1);
                        continue;
                    }
                }

                window[unpPtr++] = (byte)Ch;
                continue;
            }

            var Number = this.decodeNumber(LD);
            if (Number < 256)
            {
                window[unpPtr++] = (byte)Number;
                continue;
            }

            if (Number >= 271)
            {
                var Length = LDecode[Number -= 271] + 3;
                if ((Bits = LBits[Number]) > 0)
                {
                    Length += Utility.URShift(GetBits(), (16 - Bits));
                    AddBits(Bits);
                }

                var DistNumber = this.decodeNumber(DD);
                var Distance = DDecode[DistNumber] + 1;
                if ((Bits = DBits[DistNumber]) > 0)
                {
                    if (DistNumber > 9)
                    {
                        if (Bits > 4)
                        {
                            Distance += ((Utility.URShift(GetBits(), (20 - Bits))) << 4);
                            AddBits(Bits - 4);
                        }

                        if (lowDistRepCount > 0)
                        {
                            lowDistRepCount--;
                            Distance += prevLowDist;
                        }
                        else
                        {
                            var LowDist = this.decodeNumber(LDD);
                            if (LowDist == 16)
                            {
                                lowDistRepCount = PackDef.LOW_DIST_REP_COUNT - 1;
                                Distance += prevLowDist;
                            }
                            else
                            {
                                Distance += LowDist;
                                prevLowDist = LowDist;
                            }
                        }
                    }
                    else
                    {
                        Distance += Utility.URShift(GetBits(), (16 - Bits));
                        AddBits(Bits);
                    }
                }

                if (Distance >= 0x2000)
                {
                    Length++;
                    if (Distance >= 0x40000L)
                    {
                        Length++;
                    }
                }

                InsertOldDist(Distance);
                InsertLastMatch(Length, Distance);

                CopyString(Length, Distance);
                continue;
            }

            if (Number == 256)
            {
                if (!ReadEndOfBlock())
                {
                    break;
                }

                continue;
            }

            if (Number == 257)
            {
                if (!ReadVMCode())
                {
                    break;
                }

                continue;
            }

            if (Number == 258)
            {
                if (lastLength != 0)
                {
                    CopyString(lastLength, lastDist);
                }

                continue;
            }

            if (Number < 263)
            {
                var DistNum = Number - 259;
                var Distance = oldDist[DistNum];
                for (var I = DistNum; I > 0; I--)
                {
                    oldDist[I] = oldDist[I - 1];
                }

                oldDist[0] = Distance;

                var LengthNumber = this.decodeNumber(RD);
                var Length = LDecode[LengthNumber] + 2;
                if ((Bits = LBits[LengthNumber]) > 0)
                {
                    Length += Utility.URShift(GetBits(), (16 - Bits));
                    AddBits(Bits);
                }

                InsertLastMatch(Length, Distance);
                CopyString(Length, Distance);
                continue;
            }

            if (Number < 272)
            {
                var Distance = SDDecode[Number -= 263] + 1;
                if ((Bits = SDBits[Number]) > 0)
                {
                    Distance += Utility.URShift(GetBits(), (16 - Bits));
                    AddBits(Bits);
                }

                InsertOldDist(Distance);
                InsertLastMatch(2, Distance);
                CopyString(2, Distance);
            }
        }

        UnpWriteBuf();
    }

    private void UnpWriteBuf()
    {
        var WrittenBorder = wrPtr;
        var WriteSize = (unpPtr - WrittenBorder) & PackDef.MAXWINMASK;
        for (var I = 0; I < prgStack.Count; I++)
        {
            var flt = prgStack[I];
            if (flt is null)
            {
                continue;
            }

            if (flt.NextWindow)
            {
                flt.NextWindow = false; // ->NextWindow=false;
                continue;
            }

            var BlockStart = flt.BlockStart; // ->BlockStart;
            var BlockLength = flt.BlockLength; // ->BlockLength;
            if (((BlockStart - WrittenBorder) & PackDef.MAXWINMASK) < WriteSize)
            {
                if (WrittenBorder != BlockStart)
                {
                    UnpWriteArea(WrittenBorder, BlockStart);
                    WrittenBorder = BlockStart;
                    WriteSize = (unpPtr - WrittenBorder) & PackDef.MAXWINMASK;
                }

                if (BlockLength <= WriteSize)
                {
                    var BlockEnd = (BlockStart + BlockLength) & PackDef.MAXWINMASK;
                    if (BlockStart < BlockEnd || BlockEnd == 0)
                    {
                        // VM.SetMemory(0,Window+BlockStart,BlockLength);
                        rarVM.setMemory(0, window, BlockStart, BlockLength);
                    }
                    else
                    {
                        var FirstPartLength = PackDef.MAXWINSIZE - BlockStart;

                        // VM.SetMemory(0,Window+BlockStart,FirstPartLength);
                        rarVM.setMemory(0, window, BlockStart, FirstPartLength);

                        // VM.SetMemory(FirstPartLength,Window,BlockEnd);
                        rarVM.setMemory(FirstPartLength, window, 0, BlockEnd);
                    }

                    var Prg = flt.Program;
                    ExecuteCode(Prg);

                    var FilteredDataOffset = Prg.FilteredDataOffset;
                    var FilteredDataSize = Prg.FilteredDataSize;
                    var FilteredData = ArrayPool<byte>.Shared.Rent(FilteredDataSize);
                    try
                    {
                        Array.Copy(
                            rarVM.Mem,
                            FilteredDataOffset,
                            FilteredData,
                            0,
                            FilteredDataSize
                        );

                        prgStack[I] = null;
                        while (I + 1 < prgStack.Count)
                        {
                            var NextFilter = prgStack[I + 1];
                            if (
                                NextFilter is null
                                || NextFilter.BlockStart != BlockStart
                                || NextFilter.BlockLength != FilteredDataSize
                                || NextFilter.NextWindow
                            )
                            {
                                break;
                            }

                            // apply several filters to same data block

                            rarVM.setMemory(0, FilteredData, 0, FilteredDataSize);

                            // .SetMemory(0,FilteredData,FilteredDataSize);

                            var NextPrg = NextFilter.Program;
                            ExecuteCode(NextPrg);

                            FilteredDataOffset = NextPrg.FilteredDataOffset;
                            FilteredDataSize = NextPrg.FilteredDataSize;
                            if (FilteredData.Length < FilteredDataSize)
                            {
                                // Keep ownership of the old buffer if renting its replacement fails.
                                var replacement = ArrayPool<byte>.Shared.Rent(FilteredDataSize);
                                ArrayPool<byte>.Shared.Return(FilteredData);
                                FilteredData = replacement;
                            }

                            for (var i = 0; i < FilteredDataSize; i++)
                            {
                                FilteredData[i] = rarVM.Mem[FilteredDataOffset + i];
                            }

                            I++;
                            prgStack[I] = null;
                        }

                        writeStream.Write(
                            FilteredData,
                            0,
                            (int)Math.Min(FilteredDataSize, Math.Max(0, destUnpSize))
                        );
                        writtenFileSize += FilteredDataSize;
                        destUnpSize -= FilteredDataSize;
                        WrittenBorder = BlockEnd;
                        WriteSize = (unpPtr - WrittenBorder) & PackDef.MAXWINMASK;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(FilteredData);
                    }
                }
                else
                {
                    for (var J = I; J < prgStack.Count; J++)
                    {
                        var filt = prgStack[J];
                        if (filt != null && filt.NextWindow)
                        {
                            filt.NextWindow = false;
                        }
                    }

                    wrPtr = WrittenBorder;
                    return;
                }
            }
        }

        UnpWriteArea(WrittenBorder, unpPtr);
        wrPtr = unpPtr;
    }

    private void UnpWriteArea(int startPtr, int endPtr)
    {
        if (endPtr < startPtr)
        {
            UnpWriteData(window, startPtr, -startPtr & PackDef.MAXWINMASK);
            UnpWriteData(window, 0, endPtr);
        }
        else
        {
            UnpWriteData(window, startPtr, endPtr - startPtr);
        }
    }

    private void UnpWriteData(byte[] data, int offset, int size)
    {
        // allow destUnpSize == 0 here to ensure that 0 size writes
        // go through RarStream's Write so that Suspended is set correctly
        if (destUnpSize < 0)
        {
            return;
        }

        var writeSize = size;
        if (writeSize > destUnpSize)
        {
            writeSize = (int)destUnpSize;
        }

        writeStream.Write(data, offset, writeSize);

        writtenFileSize += size;
        destUnpSize -= size;
    }

    private void InsertOldDist(uint distance) =>
        // TODO uint
        InsertOldDist((int)distance);

    private void InsertOldDist(int distance)
    {
        oldDist[3] = oldDist[2];
        oldDist[2] = oldDist[1];
        oldDist[1] = oldDist[0];
        oldDist[0] = distance;
    }

    private void InsertLastMatch(int length, int distance)
    {
        lastDist = distance;
        lastLength = length;
    }

    private void CopyString(uint length, uint distance) =>
        // TODO uint
        CopyString((int)length, (int)distance);

    private void CopyString(int length, int distance)
    {
        ValidateDistance(distance);
        firstWindowDone |= unpPtr + length >= PackDef.MAXWINSIZE;
        // System.out.println("copyString(" + length + ", " + distance + ")");

        var destPtr = unpPtr - distance;
        var safeZone = PackDef.MAXWINSIZE - length;

        // Fast path: use Array.Copy for bulk operations when in safe zone
        if (destPtr >= 0 && destPtr < safeZone && unpPtr < safeZone && distance >= length)
        {
            // Non-overlapping copy: can use Array.Copy directly
            Array.Copy(window, destPtr, window, unpPtr, length);
            unpPtr += length;
        }
        else if (destPtr >= 0 && destPtr < safeZone && unpPtr < safeZone)
        {
            // Overlapping copy in safe zone: use byte-by-byte to handle self-referential copies
            for (int i = 0; i < length; i++)
            {
                window[unpPtr + i] = window[destPtr + i];
            }

            unpPtr += length;
        }
        else
        {
            // Slow path with wraparound mask
            while (length-- != 0)
            {
                window[unpPtr] = window[destPtr++ & PackDef.MAXWINMASK];
                unpPtr = (unpPtr + 1) & PackDef.MAXWINMASK;
            }
        }
    }

    private void UnpInitData(bool solid)
    {
        if (!solid)
        {
            tablesRead = false;
            new Span<int>(oldDist).Clear(); // memset(oldDist,0,sizeof(OldDist));

            oldDistPtr = 0;
            lastDist = 0;
            lastLength = 0;

            new Span<byte>(unpOldTable).Clear(); // memset(UnpOldTable,0,sizeof(UnpOldTable));

            unpPtr = 0;
            wrPtr = 0;
            PpmEscChar = 2;
            WriteBorder = Math.Min(MaxWinSize, UNPACK_MAX_WRITE) & MaxWinMask;

            InitFilters();
        }

        InitBitInput();
        ppmError = false;
        writtenFileSize = 0;
        readTop = 0;
        readBorder = 0;
        unpInitData20(solid);
    }

    //void Unpack::UnpInitData(bool Solid)
    //{
    //  if (!Solid)
    //  {
    //    memset(OldDist,0,sizeof(OldDist));
    //    OldDistPtr=0;
    //    LastDist=LastLength=0;
    ////    memset(Window,0,MaxWinSize);
    //    memset(&BlockTables,0,sizeof(BlockTables));
    //    UnpPtr=WrPtr=0;
    //    WriteBorder=Min(MaxWinSize,UNPACK_MAX_WRITE)&MaxWinMask;
    //  }
    //  // Filters never share several solid files, so we can safely reset them
    //  // even in solid archive.
    //  InitFilters();
    //
    //  Inp.InitBitInput();
    //  WrittenFileSize=0;
    //  ReadTop=0;
    //  ReadBorder=0;
    //
    //  memset(&BlockHeader,0,sizeof(BlockHeader));
    //  BlockHeader.BlockSize=-1;  // '-1' means not defined yet.
    //#ifndef SFX_MODULE
    //  UnpInitData20(Solid);
    //#endif
    //  UnpInitData30(Solid);
    //  UnpInitData50(Solid);
    //}

    private void InitFilters()
    {
        oldFilterLengths.Clear();
        lastFilter = 0;

        filters.Clear();

        prgStack.Clear();
    }

    private bool ReadEndOfBlock()
    {
        var BitField = GetBits();
        bool NewTable,
            NewFile = false;
        if ((BitField & 0x8000) != 0)
        {
            NewTable = true;
            AddBits(1);
        }
        else
        {
            NewFile = true;
            NewTable = (BitField & 0x4000) != 0;
            AddBits(2);
        }

        tablesRead = !NewTable;
        return !(NewFile || NewTable && !ReadTables());
    }

    private bool ReadTables()
    {
        Span<byte> bitLength = stackalloc byte[PackDef.BC];
        Span<byte> table = stackalloc byte[PackDef.HUFF_TABLE_SIZE];

        if (inAddr > readTop - 25)
        {
            if (!unpReadBuf())
            {
                return (false);
            }
        }

        AddBits((8 - inBit) & 7);
        long bitField = GetBits() & unchecked((int)0xffFFffFF);
        if ((bitField & 0x8000) != 0)
        {
            unpBlockType = BlockTypes.BLOCK_PPM;
            return (ppm.DecodeInit(this, PpmEscChar));
        }

        unpBlockType = BlockTypes.BLOCK_LZ;

        prevLowDist = 0;
        lowDistRepCount = 0;

        if ((bitField & 0x4000) == 0)
        {
            new Span<byte>(unpOldTable).Clear(); // memset(UnpOldTable,0,sizeof(UnpOldTable));
        }

        AddBits(2);

        for (var i = 0; i < PackDef.BC; i++)
        {
            var length = (Utility.URShift(GetBits(), 12)) & 0xFF;
            AddBits(4);
            if (length == 15)
            {
                var zeroCount = (Utility.URShift(GetBits(), 12)) & 0xFF;
                AddBits(4);
                if (zeroCount == 0)
                {
                    bitLength[i] = 15;
                }
                else
                {
                    zeroCount += 2;
                    while (zeroCount-- > 0 && i < bitLength.Length)
                    {
                        bitLength[i++] = 0;
                    }

                    i--;
                }
            }
            else
            {
                bitLength[i] = (byte)length;
            }
        }

        UnpackUtility.makeDecodeTables(bitLength, 0, BD, PackDef.BC);

        var TableSize = PackDef.HUFF_TABLE_SIZE;

        for (var i = 0; i < TableSize; )
        {
            if (inAddr > readTop - 5)
            {
                if (!unpReadBuf())
                {
                    return (false);
                }
            }

            var Number = this.decodeNumber(BD);
            if (Number < 16)
            {
                table[i] = (byte)((Number + unpOldTable[i]) & 0xf);
                i++;
            }
            else if (Number < 18)
            {
                int N;
                if (Number == 16)
                {
                    N = (Utility.URShift(GetBits(), 13)) + 3;
                    AddBits(3);
                }
                else
                {
                    N = (Utility.URShift(GetBits(), 9)) + 11;
                    AddBits(7);
                }

                while (N-- > 0 && i < TableSize)
                {
                    if (i == 0)
                    {
                        throw new InvalidFormatException(
                            "RAR table cannot repeat a missing previous length."
                        );
                    }
                    table[i] = table[i - 1];
                    i++;
                }
            }
            else
            {
                int N;
                if (Number == 18)
                {
                    N = (Utility.URShift(GetBits(), 13)) + 3;
                    AddBits(3);
                }
                else
                {
                    N = (Utility.URShift(GetBits(), 9)) + 11;
                    AddBits(7);
                }

                while (N-- > 0 && i < TableSize)
                {
                    table[i++] = 0;
                }
            }
        }

        tablesRead = true;
        if (inAddr > readTop)
        {
            return (false);
        }

        UnpackUtility.makeDecodeTables(table, 0, LD, PackDef.NC);
        UnpackUtility.makeDecodeTables(table, PackDef.NC, DD, PackDef.DC);
        UnpackUtility.makeDecodeTables(table, PackDef.NC + PackDef.DC, LDD, PackDef.LDC);
        UnpackUtility.makeDecodeTables(
            table,
            PackDef.NC + PackDef.DC + PackDef.LDC,
            RD,
            PackDef.RC
        );

        // memcpy(unpOldTable,table,sizeof(unpOldTable));

        table.CopyTo(unpOldTable);
        return (true);
    }

    private bool ReadVMCode()
    {
        var FirstByte = GetBits() >> 8;
        AddBits(8);
        var Length = (FirstByte & 7) + 1;
        if (Length == 7)
        {
            Length = (GetBits() >> 8) + 7;
            AddBits(8);
        }
        else if (Length == 8)
        {
            Length = GetBits();
            AddBits(16);
        }

        var vmCode = new List<byte>();
        for (var I = 0; I < Length; I++)
        {
            if (inAddr >= readTop - 1 && !unpReadBuf() && I < Length - 1)
            {
                return (false);
            }

            vmCode.Add((byte)(GetBits() >> 8));
            AddBits(8);
        }

        return AddVMCode(FirstByte, vmCode);
    }

    private bool ReadVMCodePPM()
    {
        var FirstByte = ppm.DecodeChar();
        if (FirstByte == -1)
        {
            return (false);
        }

        var Length = (FirstByte & 7) + 1;
        if (Length == 7)
        {
            var B1 = ppm.DecodeChar();
            if (B1 == -1)
            {
                return (false);
            }

            Length = B1 + 7;
        }
        else if (Length == 8)
        {
            var B1 = ppm.DecodeChar();
            if (B1 == -1)
            {
                return (false);
            }

            var B2 = ppm.DecodeChar();
            if (B2 == -1)
            {
                return (false);
            }

            Length = (B1 * 256) + B2;
        }

        var vmCode = new List<byte>();
        for (var I = 0; I < Length; I++)
        {
            var Ch = ppm.DecodeChar();
            if (Ch == -1)
            {
                return (false);
            }

            vmCode.Add((byte)Ch); // VMCode[I]=Ch;
        }

        return AddVMCode(FirstByte, vmCode);
    }

    private bool AddVMCode(int firstByte, List<byte> vmCode)
    {
        if (vmCode.Count == 0 || vmCode.Count > MAX_SIZE)
        {
            throw new InvalidFormatException("Invalid RAR filter record length.");
        }
        using var Inp = new BitInput();
        Inp.InitBitInput();

        // memcpy(Inp.InBuf,Code,Min(BitInput::MAX_SIZE,CodeSize));
        for (var i = 0; i < Math.Min(MAX_SIZE, vmCode.Count); i++)
        {
            Inp.InBuf[i] = vmCode[i];
        }

        int FiltPos;
        if ((firstByte & 0x80) != 0)
        {
            FiltPos = RarVM.ReadData(Inp);
            if (FiltPos == 0)
            {
                InitFilters();
            }
            else
            {
                FiltPos--;
            }
        }
        else
        {
            FiltPos = lastFilter; // use the same filter as last time
        }

        if (FiltPos < 0 || FiltPos > filters.Count || FiltPos > oldFilterLengths.Count)
        {
            throw new InvalidFormatException("Invalid RAR filter position.");
        }

        lastFilter = FiltPos;
        var NewFilter = (FiltPos == filters.Count);

        var StackFilter = new UnpackFilter(); // new filter for

        // PrgStack

        UnpackFilter Filter;
        if (NewFilter)
        // new filter code, never used before since VM reset
        {
            // too many different filters, corrupt archive
            if (FiltPos > 1024)
            {
                throw new InvalidFormatException("Invalid RAR filter program.");
            }

            // Filters[Filters.Size()-1]=Filter=new UnpackFilter;
            Filter = new UnpackFilter();
            filters.Add(Filter);
            StackFilter.ParentFilter = filters.Count - 1;
            oldFilterLengths.Add(0);
            Filter.ExecCount = 0;
        }
        // filter was used in the past
        else
        {
            Filter = filters[FiltPos];
            StackFilter.ParentFilter = FiltPos;
            Filter.ExecCount++; // ->ExecCount++;
        }

        // Reclaim executed slots before enforcing UnRAR's bounded filter stack.
        prgStack.RemoveAll(filter => filter is null);
        if (prgStack.Count >= 8192)
        {
            throw new InvalidFormatException("Too many RAR filters.");
        }
        prgStack.Add(StackFilter);
        StackFilter.ExecCount = Filter.ExecCount; // ->ExecCount;

        var BlockStart = RarVM.ReadData(Inp);
        if ((firstByte & 0x40) != 0)
        {
            BlockStart += 258;
        }

        StackFilter.BlockStart = ((BlockStart + unpPtr) & PackDef.MAXWINMASK);
        if ((firstByte & 0x20) != 0)
        {
            StackFilter.BlockLength = RarVM.ReadData(Inp);
        }
        else
        {
            StackFilter.BlockLength =
                FiltPos < oldFilterLengths.Count ? oldFilterLengths[FiltPos] : 0;
        }

        if ((uint)StackFilter.BlockLength > RarVM.VM_MEMSIZE)
        {
            throw new InvalidFormatException("Invalid RAR filter block length.");
        }

        StackFilter.NextWindow =
            (wrPtr != unpPtr) && ((wrPtr - unpPtr) & PackDef.MAXWINMASK) <= BlockStart;

        // DebugLog("\nNextWindow: UnpPtr=%08x WrPtr=%08x
        // BlockStart=%08x",UnpPtr,WrPtr,BlockStart);

        oldFilterLengths[FiltPos] = StackFilter.BlockLength;

        // memset(StackFilter->Prg.InitR,0,sizeof(StackFilter->Prg.InitR));
        new Span<int>(StackFilter.Program.InitR).Clear();
        StackFilter.Program.InitR[3] = RarVM.VM_GLOBALMEMADDR; // StackFilter->Prg.InitR[3]=VM_GLOBALMEMADDR;
        StackFilter.Program.InitR[4] = StackFilter.BlockLength;

        // StackFilter->Prg.InitR[4]=StackFilter->BlockLength;
        StackFilter.Program.InitR[5] = StackFilter.ExecCount; // StackFilter->Prg.InitR[5]=StackFilter->ExecCount;

        if ((firstByte & 0x10) != 0)
        // set registers to optional parameters
        // if any
        {
            var InitMask = Utility.URShift(Inp.GetBits(), 9);
            Inp.AddBits(7);
            for (var I = 0; I < 7; I++)
            {
                if ((InitMask & (1 << I)) != 0)
                {
                    // StackFilter->Prg.InitR[I]=RarVM::ReadData(Inp);
                    StackFilter.Program.InitR[I] = RarVM.ReadData(Inp);
                }
            }
        }

        if (NewFilter)
        {
            var VMCodeSize = RarVM.ReadData(Inp);
            if (VMCodeSize >= 0x10000 || VMCodeSize <= 0)
            {
                throw new InvalidFormatException("Invalid RAR filter program.");
            }

            var VMCode = ArrayPool<byte>.Shared.Rent(VMCodeSize);
            try
            {
                for (var I = 0; I < VMCodeSize; I++)
                {
                    if (Inp.inAddr * 8 + Inp.inBit + 8 > vmCode.Count * 8)
                    {
                        throw new InvalidFormatException("Invalid RAR filter program.");
                    }

                    VMCode[I] = (byte)(Inp.GetBits() >> 8);
                    Inp.AddBits(8);
                }

                // VM.Prepare(&VMCode[0],VMCodeSize,&Filter->Prg);
                rarVM.prepare(VMCode.AsSpan(0, VMCodeSize), Filter.Program);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(VMCode);
            }
        }

        StackFilter.Program.Type = Filter.Program.Type;
        // The input block size is authoritative. Optional registers must not
        // expose bytes outside the block copied into VM memory.
        if ((uint)StackFilter.Program.InitR[4] > StackFilter.BlockLength)
        {
            throw new InvalidFormatException("Invalid RAR filter input length.");
        }
        if (Inp.inAddr * 8 + Inp.inBit > vmCode.Count * 8)
        {
            throw new InvalidFormatException("Truncated RAR filter record.");
        }
        return true;
    }

    private void ExecuteCode(VMPreparedProgram prg)
    {
        prg.InitR[6] = (int)writtenFileSize;
        rarVM.execute(prg);
    }

    private long oldWrittenSize;

    private void WriteOldData(byte[] data, int offset, int count)
    {
        count = (int)Math.Min(count, Math.Max(0, fileHeader.UncompressedSize - oldWrittenSize));
        writeStream.Write(data, offset, count);
        oldWrittenSize += count;
    }

    private void CleanUp()
    {
        ppm.Dispose();
    }
}
