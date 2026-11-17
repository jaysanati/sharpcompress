using System;
using System.Buffers;
using SharpCompress.Common;

namespace SharpCompress.Compressors.Rar.VM;

internal class BitInput : IDisposable
{
    /// <summary> the max size of the input</summary>
    internal const int MAX_SIZE = 0x8000;

    public int inAddr;
    public int inBit;

    // TODO: rename var
    public int InAddr
    {
        get => inAddr;
        set => inAddr = value;
    }
    public int InBit
    {
        get => inBit;
        set => inBit = value;
    }
    private readonly byte[] _privateBuffer = ArrayPool<byte>.Shared.Rent(MAX_SIZE + 3);
    private bool _disposed;

    /// <summary>  </summary>
    internal BitInput()
    {
        InBuf = _privateBuffer;
        Array.Clear(InBuf, 0, InBuf.Length);
    }

    internal byte[] InBuf { get; }

    internal void InitBitInput()
    {
        inAddr = 0;
        inBit = 0;
    }

    internal void faddbits(uint bits) =>
        // TODO uint
        AddBits((int)bits);

    /// <summary>
    /// also named faddbits
    /// </summary>
    /// <param name="bits"></param>
    internal void AddBits(int bits)
    {
        bits += inBit;
        inAddr += (bits >> 3);
        inBit = bits & 7;
    }

    internal uint fgetbits() =>
        // TODO uint
        (uint)GetBits();

    internal uint getbits() =>
        // TODO uint
        (uint)GetBits();

    /// <summary>
    /// (also named fgetbits)
    /// </summary>
    /// <returns>
    /// the bits (unsigned short)
    /// </returns>
    internal int GetBits()
    {
        if ((uint)inAddr >= MAX_SIZE)
        {
            throw new InvalidFormatException("Truncated RAR bit input.");
        }
        // Three zero lookahead bytes allow a final partial byte to be read
        // without indexing past the logical buffer boundary.
        return ((InBuf[inAddr] << 16 | InBuf[inAddr + 1] << 8 | InBuf[inAddr + 2]) >> (8 - inBit))
            & 0xffff;
    }

    /// <summary> Indicates an Overfow</summary>
    /// <param name="IncPtr">how many bytes to inc
    /// </param>
    /// <returns> true if an Oververflow would occur
    /// </returns>
    internal bool Overflow(int IncPtr) => (inAddr + IncPtr >= MAX_SIZE);

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        ArrayPool<byte>.Shared.Return(_privateBuffer);
        _disposed = true;
    }
}
