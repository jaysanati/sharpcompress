using System;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarCrcStream : RarStream
{
    private readonly string? _key;
    private readonly MultiVolumeReadOnlyStreamBase _readStream;
    private uint _currentCrc;

    private RarCrcStream(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStreamBase readStream
    )
        : base(unpack, fileHeader, readStream)
    {
        this._readStream = readStream;
        _key = fileHeader.FileName;
        ResetCrc();
    }

    public static RarCrcStream Create(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStream readStream
    )
    {
        var stream = new RarCrcStream(unpack, fileHeader, readStream);
        return stream;
    }

    // Async methods moved to RarCrcStream.Async.cs
    public uint GetCrc() => ~_currentCrc;

    public void ResetCrc() => _currentCrc = 0xffffffff;

    private void VerifyCrc(int result, int count)
    {
        if (
            count == 0
            || (result != 0 && !ReachedDeclaredSize)
            || _readStream.CurrentCrc is not { } crc
        )
        {
            return;
        }
        var value = GetCrc();
        var raw = new byte[]
        {
            (byte)value,
            (byte)(value >> 8),
            (byte)(value >> 16),
            (byte)(value >> 24),
        };
        if (!_readStream.ConvertHashToMac(raw).AsSpan().SequenceEqual(crc))
        {
            if (IsEncrypted)
            {
                throw new CryptographicException("Wrong password or damaged RAR data: " + _key);
            }
            throw new InvalidFormatException("file crc mismatch: " + _key);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var result = base.Read(buffer, offset, count);
        if (result != 0)
        {
            _currentCrc = RarCRC.CheckCrc(_currentCrc, buffer, offset, result);
        }
        VerifyCrc(result, count);

        return result;
    }
}
