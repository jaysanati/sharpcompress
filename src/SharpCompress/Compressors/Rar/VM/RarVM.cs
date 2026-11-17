using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using SharpCompress.Common;

namespace SharpCompress.Compressors.Rar.VM;

internal sealed class RarVM : BitInput
{
    //private void  InitBlock()
    //{
    //    Mem.set_Renamed(offset + 0, Byte.valueOf((sbyte) (value_Renamed & 0xff)));
    //    Mem.set_Renamed(offset + 1, Byte.valueOf((sbyte) ((Utility.URShift(value_Renamed, 8)) & 0xff)));
    //    Mem.set_Renamed(offset + 2, Byte.valueOf((sbyte) ((Utility.URShift(value_Renamed, 16)) & 0xff)));
    //    Mem.set_Renamed(offset + 3, Byte.valueOf((sbyte) ((Utility.URShift(value_Renamed, 24)) & 0xff)));

    //}
    internal byte[] Mem => _memory.NotNull();

    private byte[]? _memory = ArrayPool<byte>.Shared.Rent(VM_MEMSIZE + 4);

    public const int VM_MEMSIZE = 0x40000;

    //UPGRADE_NOTE: Final was removed from the declaration of 'VM_MEMMASK '. "ms-help://MS.VSCC.v80/dv_commoner/local/redirect.htm?index='!DefaultContextWindowIndex'&keyword='jlca1003'"
    public static readonly int VM_MEMMASK = (VM_MEMSIZE - 1);

    public const int VM_GLOBALMEMADDR = 0x3C000;

    public const int VM_GLOBALMEMSIZE = 0x2000;

    public const int VM_FIXEDGLOBALSIZE = 64;

    private const int regCount = 8;
    private static readonly VMStandardFilterSignature[] StandardFilterSignatures =
    {
        new(53, 0xad576887, VMStandardFilters.VMSF_E8),
        new(57, 0x3cd7e57e, VMStandardFilters.VMSF_E8E9),
        new(120, 0x3769893f, VMStandardFilters.VMSF_ITANIUM),
        new(29, 0x0e06077d, VMStandardFilters.VMSF_DELTA),
        new(149, 0x1c2c5dc8, VMStandardFilters.VMSF_RGB),
        new(216, 0xbc85e701, VMStandardFilters.VMSF_AUDIO),
    };

    private readonly int[] R = new int[regCount];

    internal RarVM() => Array.Clear(Mem, 0, Mem.Length);

    public override void Dispose()
    {
        base.Dispose();
        if (_memory is null)
        {
            return;
        }
        ArrayPool<byte>.Shared.Return(_memory);
        _memory = null;
    }

    private bool IsVMMem(byte[] mem) => Mem == mem;

    private int GetValue(bool byteMode, byte[] mem, int offset)
    {
        if (byteMode)
        {
            if (IsVMMem(mem))
            {
                return (mem[offset]);
            }
            return (mem[offset] & 0xff);
        }
        if (IsVMMem(mem))
        {
            return BinaryPrimitives.ReadInt32LittleEndian(mem.AsSpan(offset));
        }
        return BinaryPrimitives.ReadInt32BigEndian(mem.AsSpan(offset));
    }

    private void SetValue(bool byteMode, byte[] mem, int offset, int value)
    {
        if (byteMode)
        {
            if (IsVMMem(mem))
            {
                mem[offset] = (byte)value;
            }
            else
            {
                mem[offset] = (byte)((mem[offset] & 0x00) | (byte)(value & 0xff));
            }
        }
        else
        {
            if (IsVMMem(mem))
            {
                BinaryPrimitives.WriteInt32LittleEndian(mem.AsSpan(offset), value);
            }
            else
            {
                BinaryPrimitives.WriteInt32BigEndian(mem.AsSpan(offset), value);
            }
        }

        // #define SET_VALUE(ByteMode,Addr,Value) SetValue(ByteMode,(uint
        // *)Addr,Value)
    }

    internal void SetLowEndianValue(List<byte> mem, int offset, int value)
    {
        mem[offset + 0] = (byte)(value & 0xff);
        mem[offset + 1] = (byte)(Utility.URShift(value, 8) & 0xff);
        mem[offset + 2] = (byte)(Utility.URShift(value, 16) & 0xff);
        mem[offset + 3] = (byte)(Utility.URShift(value, 24) & 0xff);
    }

    public void execute(VMPreparedProgram prg)
    {
        Array.Copy(prg.InitR, R, prg.InitR.Length);
        var size = R[4];
        if ((uint)size > VM_MEMSIZE)
        {
            throw new InvalidFormatException("Invalid RAR filter block length.");
        }
        var separateOutput =
            prg.Type
            is VMStandardFilters.VMSF_DELTA
                or VMStandardFilters.VMSF_RGB
                or VMStandardFilters.VMSF_AUDIO;
        if (separateOutput && size > VM_MEMSIZE / 2)
        {
            throw new InvalidFormatException("Invalid RAR filter output length.");
        }
        if (
            prg.Type is VMStandardFilters.VMSF_DELTA or VMStandardFilters.VMSF_AUDIO
            && (R[0] <= 0 || R[0] > 1024)
        )
        {
            throw new InvalidFormatException("Invalid RAR filter channel count.");
        }
        if (
            prg.Type == VMStandardFilters.VMSF_RGB
            && (size < 3 || (uint)(R[0] - 3) > size || (uint)R[1] > 2)
        )
        {
            throw new InvalidFormatException("Invalid RAR RGB filter width or position.");
        }
        if (prg.Type == VMStandardFilters.VMSF_NONE)
        {
            throw new InvalidFormatException("Unsupported RAR VM program.");
        }
        if (prg.Type == VMStandardFilters.VMSF_RGB)
        {
            // Very narrow RGB rows can predict from not-yet-written output.
            // Do not let those reads depend on a preceding filter or entry.
            Array.Clear(Mem, size, size);
        }
        ExecuteStandardFilter(prg.Type);
        prg.FilteredDataOffset = separateOutput ? size : 0;
        prg.FilteredDataSize = size;
    }

    public void prepare(ReadOnlySpan<byte> code, VMPreparedProgram prg)
    {
        // UnRAR 5.5 and later recognize standard filters only. Never interpret
        // archive-supplied instructions or operand addresses.
        byte checksum = 0;
        for (var i = 1; i < code.Length; i++)
        {
            checksum ^= code[i];
        }
        prg.Type =
            code.Length > 0 && checksum == code[0]
                ? IsStandardFilter(code)
                : VMStandardFilters.VMSF_NONE;
        if (prg.Type == VMStandardFilters.VMSF_NONE)
        {
            throw new InvalidFormatException("Unsupported RAR VM program.");
        }
    }

    internal static int ReadData(BitInput rarVM)
    {
        var data = rarVM.GetBits();
        switch (data & 0xc000)
        {
            case 0:
                rarVM.AddBits(6);
                return ((data >> 10) & 0xf);

            case 0x4000:
                if ((data & 0x3c00) == 0)
                {
                    data = unchecked((int)0xffffff00) | ((data >> 2) & 0xff);
                    rarVM.AddBits(14);
                }
                else
                {
                    data = (data >> 6) & 0xff;
                    rarVM.AddBits(10);
                }
                return (data);

            case 0x8000:
                rarVM.AddBits(2);
                data = rarVM.GetBits();
                rarVM.AddBits(16);
                return (data);

            default:
                rarVM.AddBits(2);
                data = (rarVM.GetBits() << 16);
                rarVM.AddBits(16);
                data |= rarVM.GetBits();
                rarVM.AddBits(16);
                return (data);
        }
    }

    private VMStandardFilters IsStandardFilter(ReadOnlySpan<byte> code)
    {
        var CodeCRC = RarCRC.CheckCrc(0xffffffff, code, 0, code.Length) ^ 0xffffffff;
        for (var i = 0; i < StandardFilterSignatures.Length; i++)
        {
            if (
                StandardFilterSignatures[i].CRC == CodeCRC
                && StandardFilterSignatures[i].Length == code.Length
            )
            {
                return (StandardFilterSignatures[i].Type);
            }
        }
        return (VMStandardFilters.VMSF_NONE);
    }

    private void ExecuteStandardFilter(VMStandardFilters filterType)
    {
        var mem = Mem;
        switch (filterType)
        {
            case VMStandardFilters.VMSF_E8:
            case VMStandardFilters.VMSF_E8E9:
                {
                    var dataSize = R[4];
                    long fileOffset = R[6] & unchecked((int)0xFFffFFff);

                    if (dataSize > VM_MEMSIZE)
                    {
                        break;
                    }
                    var fileSize = 0x1000000;
                    var cmpByte2 = (byte)(
                        (filterType == VMStandardFilters.VMSF_E8E9) ? 0xe9 : 0xe8
                    );
                    for (var curPos = 0; curPos < dataSize - 4; )
                    {
                        var curByte = mem[curPos++];
                        if (curByte == 0xe8 || curByte == cmpByte2)
                        {
                            //		#ifdef PRESENT_INT32
                            //		            sint32 Offset=CurPos+FileOffset;
                            //		            sint32 Addr=GET_VALUE(false,Data);
                            //		            if (Addr<0)
                            //		            {
                            //		              if (Addr+Offset>=0)
                            //		                SET_VALUE(false,Data,Addr+FileSize);
                            //		            }
                            //		            else
                            //		              if (Addr<FileSize)
                            //		                SET_VALUE(false,Data,Addr-Offset);
                            //		#else
                            var offset = curPos + fileOffset;
                            long Addr = GetValue(false, mem, curPos);
                            if ((Addr & unchecked((int)0x80000000)) != 0)
                            {
                                if (((Addr + offset) & unchecked((int)0x80000000)) == 0)
                                {
                                    SetValue(false, mem, curPos, (int)Addr + fileSize);
                                }
                            }
                            else
                            {
                                if (((Addr - fileSize) & unchecked((int)0x80000000)) != 0)
                                {
                                    SetValue(false, mem, curPos, (int)(Addr - offset));
                                }
                            }

                            //		#endif
                            curPos += 4;
                        }
                    }
                }
                break;

            case VMStandardFilters.VMSF_ITANIUM:
                {
                    var dataSize = R[4];
                    long fileOffset = R[6] & unchecked((int)0xFFffFFff);

                    if (dataSize > VM_MEMSIZE)
                    {
                        break;
                    }
                    var curPos = 0;

                    //UPGRADE_NOTE: Final was removed from the declaration of 'Masks '. "ms-help://MS.VSCC.v80/dv_commoner/local/redirect.htm?index='!DefaultContextWindowIndex'&keyword='jlca1003'"
                    byte[] Masks = { 4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0 };
                    fileOffset = Utility.URShift(fileOffset, 4);

                    while (curPos < dataSize - 21)
                    {
                        var Byte = (mem[curPos] & 0x1f) - 0x10;
                        if (Byte >= 0)
                        {
                            var cmdMask = Masks[Byte];
                            if (cmdMask != 0)
                            {
                                for (var i = 0; i <= 2; i++)
                                {
                                    if ((cmdMask & (1 << i)) != 0)
                                    {
                                        var startPos = (i * 41) + 5;
                                        var opType = filterItanium_GetBits(
                                            curPos,
                                            startPos + 37,
                                            4
                                        );
                                        if (opType == 5)
                                        {
                                            var offset = filterItanium_GetBits(
                                                curPos,
                                                startPos + 13,
                                                20
                                            );
                                            filterItanium_SetBits(
                                                curPos,
                                                (int)(offset - fileOffset) & 0xfffff,
                                                startPos + 13,
                                                20
                                            );
                                        }
                                    }
                                }
                            }
                        }
                        curPos += 16;
                        fileOffset++;
                    }
                }
                break;

            case VMStandardFilters.VMSF_DELTA:
                {
                    var dataSize = R[4] & unchecked((int)0xFFffFFff);
                    var channels = R[0] & unchecked((int)0xFFffFFff);
                    var srcPos = 0;
                    var border = (dataSize * 2) & unchecked((int)0xFFffFFff);
                    if (dataSize > VM_MEMSIZE / 2)
                    {
                        break;
                    }

                    //		 bytes from same channels are grouped to continual data blocks,
                    //		 so we need to place them back to their interleaving positions

                    for (var curChannel = 0; curChannel < channels; curChannel++)
                    {
                        byte PrevByte = 0;
                        for (
                            var destPos = dataSize + curChannel;
                            destPos < border;
                            destPos += channels
                        )
                        {
                            mem[destPos] = (PrevByte = (byte)(PrevByte - mem[srcPos++]));
                        }
                    }
                }
                break;

            case VMStandardFilters.VMSF_RGB:
                {
                    // byte *SrcData=Mem,*DestData=SrcData+DataSize;
                    int dataSize = R[4],
                        width = R[0] - 3,
                        posR = R[1];
                    var channels = 3;
                    var srcPos = 0;
                    var destDataPos = dataSize;
                    if (dataSize > VM_MEMSIZE / 2 || posR < 0)
                    {
                        break;
                    }
                    for (var curChannel = 0; curChannel < channels; curChannel++)
                    {
                        long prevByte = 0;

                        for (var i = curChannel; i < dataSize; i += channels)
                        {
                            long predicted;
                            var upperPos = i - width;
                            if (upperPos >= 3)
                            {
                                var upperDataPos = destDataPos + upperPos;
                                var upperByte = mem[upperDataPos] & 0xff;
                                var upperLeftByte = mem[upperDataPos - 3] & 0xff;
                                predicted = prevByte + upperByte - upperLeftByte;
                                var pa = Math.Abs((int)(predicted - prevByte));
                                var pb = Math.Abs((int)(predicted - upperByte));
                                var pc = Math.Abs((int)(predicted - upperLeftByte));
                                if (pa <= pb && pa <= pc)
                                {
                                    predicted = prevByte;
                                }
                                else
                                {
                                    if (pb <= pc)
                                    {
                                        predicted = upperByte;
                                    }
                                    else
                                    {
                                        predicted = upperLeftByte;
                                    }
                                }
                            }
                            else
                            {
                                predicted = prevByte;
                            }

                            prevByte = ((predicted - mem[srcPos++]) & 0xff) & 0xff;
                            mem[destDataPos + i] = (byte)(prevByte & 0xff);
                        }
                    }
                    for (int i = posR, border = dataSize - 2; i < border; i += 3)
                    {
                        var G = mem[destDataPos + i + 1];
                        mem[destDataPos + i] = (byte)(mem[destDataPos + i] + G);
                        mem[destDataPos + i + 2] = (byte)(mem[destDataPos + i + 2] + G);
                    }
                }
                break;

            case VMStandardFilters.VMSF_AUDIO:
                {
                    int dataSize = R[4],
                        channels = R[0];
                    var srcPos = 0;
                    var destDataPos = dataSize;

                    //byte *SrcData=Mem,*DestData=SrcData+DataSize;
                    if (dataSize > VM_MEMSIZE / 2)
                    {
                        break;
                    }
                    for (var curChannel = 0; curChannel < channels; curChannel++)
                    {
                        long prevByte = 0;
                        long prevDelta = 0;
                        var Dif = new long[7];
                        int D1 = 0,
                            D2 = 0,
                            D3;
                        int K1 = 0,
                            K2 = 0,
                            K3 = 0;

                        for (
                            int i = curChannel, byteCount = 0;
                            i < dataSize;
                            i += channels, byteCount++
                        )
                        {
                            D3 = D2;
                            D2 = (int)(prevDelta - D1);
                            D1 = (int)prevDelta;

                            var predicted = (8 * prevByte) + (K1 * D1) + (K2 * D2) + (K3 * D3);
                            predicted = Utility.URShift(predicted, 3) & 0xff;

                            long curByte = mem[srcPos++];

                            predicted -= curByte;
                            mem[destDataPos + i] = (byte)predicted;
                            prevDelta = (byte)(predicted - prevByte);

                            //fix java byte
                            if (prevDelta >= 128)
                            {
                                prevDelta = 0 - (256 - prevDelta);
                            }
                            prevByte = predicted;

                            //fix java byte
                            if (curByte >= 128)
                            {
                                curByte = 0 - (256 - curByte);
                            }
                            var D = ((int)curByte) << 3;

                            Dif[0] += Math.Abs(D);
                            Dif[1] += Math.Abs(D - D1);
                            Dif[2] += Math.Abs(D + D1);
                            Dif[3] += Math.Abs(D - D2);
                            Dif[4] += Math.Abs(D + D2);
                            Dif[5] += Math.Abs(D - D3);
                            Dif[6] += Math.Abs(D + D3);

                            if ((byteCount & 0x1f) == 0)
                            {
                                long minDif = Dif[0],
                                    numMinDif = 0;
                                Dif[0] = 0;
                                for (var j = 1; j < Dif.Length; j++)
                                {
                                    if (Dif[j] < minDif)
                                    {
                                        minDif = Dif[j];
                                        numMinDif = j;
                                    }
                                    Dif[j] = 0;
                                }
                                switch ((int)numMinDif)
                                {
                                    case 1:
                                        if (K1 >= -16)
                                        {
                                            K1--;
                                        }
                                        break;

                                    case 2:
                                        if (K1 < 16)
                                        {
                                            K1++;
                                        }
                                        break;

                                    case 3:
                                        if (K2 >= -16)
                                        {
                                            K2--;
                                        }
                                        break;

                                    case 4:
                                        if (K2 < 16)
                                        {
                                            K2++;
                                        }
                                        break;

                                    case 5:
                                        if (K3 >= -16)
                                        {
                                            K3--;
                                        }
                                        break;

                                    case 6:
                                        if (K3 < 16)
                                        {
                                            K3++;
                                        }
                                        break;
                                }
                            }
                        }
                    }
                }
                break;
        }
    }

    private void filterItanium_SetBits(int curPos, int bitField, int bitPos, int bitCount)
    {
        var inAddr = bitPos / 8;
        var inBit = bitPos & 7;
        var andMask = Utility.URShift(unchecked((int)0xffffffff), (32 - bitCount));
        andMask = ~(andMask << inBit);

        bitField <<= inBit;

        for (var i = 0; i < 4; i++)
        {
            Mem[curPos + inAddr + i] &= (byte)(andMask);
            Mem[curPos + inAddr + i] |= (byte)(bitField);
            andMask = (Utility.URShift(andMask, 8)) | unchecked((int)0xff000000);
            bitField = Utility.URShift(bitField, 8);
        }
    }

    private int filterItanium_GetBits(int curPos, int bitPos, int bitCount)
    {
        var inAddr = bitPos / 8;
        var inBit = bitPos & 7;
        var bitField = Mem[curPos + inAddr++] & 0xff;
        bitField |= (Mem[curPos + inAddr++] & 0xff) << 8;
        bitField |= (Mem[curPos + inAddr++] & 0xff) << 16;
        bitField |= (Mem[curPos + inAddr] & 0xff) << 24;
        bitField = Utility.URShift(bitField, inBit);
        return (bitField & (Utility.URShift(unchecked((int)0xffffffff), (32 - bitCount))));
    }

    public void setMemory(int pos, byte[] data, int offset, int dataSize)
    {
        if (pos < VM_MEMSIZE)
        {
            // Use Array.Copy for fast bulk memory operations instead of byte-by-byte loop
            // Calculate how much data can actually fit in VM memory
            int copyLength = Math.Min(dataSize, VM_MEMSIZE - pos);
            copyLength = Math.Min(copyLength, data.Length - offset);

            if (copyLength > 0)
            {
                Array.Copy(data, offset, Mem, pos, copyLength);
            }
        }
    }
}

//
