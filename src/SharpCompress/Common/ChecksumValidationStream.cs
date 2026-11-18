using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Crypto;

namespace SharpCompress.Common;

internal sealed class ChecksumValidationStream : Stream
{
    private readonly Stream _stream;
    private readonly ChecksumDescriptor _checksum;
    private readonly string _entryName;
    private readonly uint[] _crc32Table;
    private uint _seed = Crc32Stream.DEFAULT_SEED;
    private ushort _crc16;
    private bool _validated;
    private bool _skipValidation;

    private readonly long? _expectedSize;
    private readonly bool _isEncrypted;
    private readonly bool _ownsStream;
    private long _bytesRead;

    internal ChecksumValidationStream(
        Stream stream,
        ChecksumDescriptor checksum,
        string? entryName,
        long? expectedSize = null,
        bool isEncrypted = false,
        bool ownsStream = false
    )
    {
        _stream = stream;
        _expectedSize = expectedSize;
        _isEncrypted = isEncrypted;
        _ownsStream = ownsStream;
        _checksum = checksum;
        _entryName = string.IsNullOrEmpty(entryName) ? "Entry" : entryName!;
        _crc32Table = Crc32Stream.InitializeTable(Crc32Stream.DEFAULT_POLYNOMIAL);
    }

    internal void SkipValidationOnDispose()
    {
        if (_expectedSize.HasValue)
        {
            _skipValidation = true;
        }
    }

    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _stream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        if (count != 0)
        {
            UpdateAndValidateAtEof(buffer.AsSpan(offset, read), read);
        }
        return read;
    }

#if !LEGACY_DOTNET
    public override int Read(Span<byte> buffer)
    {
        var read = _stream.Read(buffer);
        if (!buffer.IsEmpty)
        {
            UpdateAndValidateAtEof(buffer[..read], read);
        }
        return read;
    }
#endif

    public override int ReadByte()
    {
        var buffer = new byte[1];
        return Read(buffer, 0, 1) == 0 ? -1 : buffer[0];
    }

    protected override void Dispose(bool disposing)
    {
        // Partial reads are allowed; disposal must never force checksum validation.
        if (disposing && _ownsStream)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var read = await _stream
            .ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        if (count != 0)
        {
            UpdateAndValidateAtEof(buffer.AsSpan(offset, read), read);
        }
        return read;
    }

#if !LEGACY_DOTNET
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!buffer.IsEmpty)
        {
            UpdateAndValidateAtEof(buffer.Span[..read], read);
        }
        return read;
    }
#endif

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private void UpdateAndValidateAtEof(ReadOnlySpan<byte> buffer, int read)
    {
        if (_skipValidation)
        {
            return;
        }
        if (read > 0)
        {
            UpdateChecksum(buffer);
            _bytesRead += read;
            if (_expectedSize == _bytesRead)
            {
                Validate();
            }
            return;
        }

        if (_expectedSize.HasValue && _bytesRead != _expectedSize.Value)
        {
            throw new EndOfStreamException($"Entry '{_entryName}' ended before its declared size.");
        }
        Validate();
    }

    private void UpdateChecksum(ReadOnlySpan<byte> buffer)
    {
        if (!_checksum.IsAvailable)
        {
            return;
        }
        switch (_checksum.Kind)
        {
            case ChecksumKind.Crc32:
            case ChecksumKind.Crc32NoFinalXor:
                _seed = Crc32Stream.CalculateCrc(_crc32Table, _seed, buffer);
                break;
            case ChecksumKind.Crc16Arc:
                _crc16 = CalculateCrc16Arc(_crc16, buffer);
                break;
        }
    }

    private void Validate()
    {
        // A missing checksum still requires size validation, but default(Crc32)
        // must not be mistaken for a stored checksum of zero.
        if (_validated || !_checksum.IsAvailable)
        {
            return;
        }

        _validated = true;

        switch (_checksum.Kind)
        {
            case ChecksumKind.Crc32:
                ValidateCrc32(finalXor: true);
                break;
            case ChecksumKind.Crc32NoFinalXor:
                ValidateCrc32(finalXor: false);
                break;
            case ChecksumKind.Crc16Arc:
                ValidateCrc16Arc();
                break;
        }
    }

    private void ValidateCrc32(bool finalXor)
    {
        var actual = finalXor ? ~_seed : _seed;
        var expected = unchecked((uint)_checksum.ExpectedValue);
        if (actual != expected)
        {
            var message =
                $"CRC mismatch for entry '{_entryName}'. Expected 0x{expected:X8}, actual 0x{actual:X8}.";
            if (_expectedSize.HasValue)
            {
                throw new ChecksumMismatchException(
                    message
                        + (
                            _isEncrypted
                                ? " The data may be corrupt or the password may be wrong."
                                : ""
                        )
                );
            }
            throw new InvalidFormatException(message);
        }
    }

    private void ValidateCrc16Arc()
    {
        var expected = unchecked((ushort)_checksum.ExpectedValue);
        if (_crc16 != expected)
        {
            throw new InvalidFormatException(
                $"CRC mismatch for entry '{_entryName}'. Expected 0x{expected:X4}, actual 0x{_crc16:X4}."
            );
        }
    }

    private static ushort CalculateCrc16Arc(ushort crc, ReadOnlySpan<byte> buffer)
    {
        foreach (var value in buffer)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}
