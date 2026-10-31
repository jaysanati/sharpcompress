using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.IO;

namespace SharpCompress.Compressors.LZMA;

/// <summary>
/// Frames one continuous LZMA encoder as LZMA2. Input and output staging allow
/// asynchronous callers to use asynchronous I/O while compression remains CPU work.
/// </summary>
internal sealed class Lzma2EncoderStream : Stream
{
    private const int MAX_UNCOMPRESSED_CHUNK_SIZE = 1 << 21;
    private readonly Stream _output;
    private readonly byte[] _buffer;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly Encoder _encoder;
    private readonly byte _lzmaPropertiesByte;
    private readonly PooledMemoryStream _packed = new();
    private readonly PooledMemoryStream _framed = new();
    private byte[]? _chunkInput;
    private int _bufferPosition;
    private bool _isFirstChunk = true;
    private bool _needsProperties = true;
    private bool _needsStateReset = true;
    private bool _isDisposed;
    private bool _faulted;

    public Lzma2EncoderStream(Stream output, LzmaEncoderProperties properties)
        : this(output, properties, ArrayPool<byte>.Shared) { }

    public Lzma2EncoderStream(Stream output, int dictionarySize, int numFastBytes)
        : this(output, dictionarySize, numFastBytes, ArrayPool<byte>.Shared) { }

    internal Lzma2EncoderStream(
        Stream output,
        int dictionarySize,
        int numFastBytes,
        ArrayPool<byte> bufferPool
    )
        : this(output, new LzmaEncoderProperties(false, dictionarySize, numFastBytes), bufferPool)
    { }

    private Lzma2EncoderStream(
        Stream output,
        LzmaEncoderProperties encoderProps,
        ArrayPool<byte> bufferPool
    )
    {
        _output = output;
        _bufferPool = bufferPool;

        _encoder = new Encoder();
        _encoder.SetCoderProperties(encoderProps.PropIDs, encoderProps.Properties);
        Span<byte> properties = stackalloc byte[5];
        _encoder.WriteCoderProperties(properties);
        _lzmaPropertiesByte = properties[0];
        Properties = [EncodeDictionarySize(encoderProps.DictionarySize)];
        // Rent after validation, so invalid properties cannot strand a buffer.
        _buffer = bufferPool.Rent(MAX_UNCOMPRESSED_CHUNK_SIZE);
    }

    public byte[] Properties { get; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_isDisposed && !_faulted;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        while (count > 0)
        {
            var toCopy = Math.Min(count, MAX_UNCOMPRESSED_CHUNK_SIZE - _bufferPosition);
            Buffer.BlockCopy(buffer, offset, _buffer, _bufferPosition, toCopy);
            _bufferPosition += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (_bufferPosition == MAX_UNCOMPRESSED_CHUNK_SIZE)
            {
                FlushChunk();
            }
        }
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toCopy = Math.Min(count, MAX_UNCOMPRESSED_CHUNK_SIZE - _bufferPosition);
            Buffer.BlockCopy(buffer, offset, _buffer, _bufferPosition, toCopy);
            _bufferPosition += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (_bufferPosition == MAX_UNCOMPRESSED_CHUNK_SIZE)
            {
                await FlushChunkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

#if !LEGACY_DOTNET
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var offset = 0;
        var count = buffer.Length;
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toCopy = Math.Min(count, MAX_UNCOMPRESSED_CHUNK_SIZE - _bufferPosition);
            buffer.Slice(offset, toCopy).Span.CopyTo(_buffer.AsSpan(_bufferPosition, toCopy));
            _bufferPosition += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (_bufferPosition == MAX_UNCOMPRESSED_CHUNK_SIZE)
            {
                await FlushChunkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }
#endif

    public override void Flush() { }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(Lzma2EncoderStream));
        }
        if (_faulted)
        {
            throw new InvalidOperationException("The LZMA2 encoder has failed.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            try
            {
                if (!_faulted)
                {
                    FlushChunk(final: true);
                    _output.WriteByte(0);
                }
            }
            finally
            {
                ReleaseBuffers();
            }
        }
        base.Dispose(disposing);
    }

#if STREAM_DISPOSEASYNC
    public override async ValueTask DisposeAsync()
#else
    public async ValueTask DisposeAsync()
#endif
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            try
            {
                if (!_faulted)
                {
                    await FlushChunkAsync(final: true).ConfigureAwait(false);
                    await _output.WriteAsync(new byte[] { 0 }, 0, 1).ConfigureAwait(false);
                }
            }
            finally
            {
                ReleaseBuffers();
            }
        }
#if STREAM_DISPOSEASYNC
        await base.DisposeAsync().ConfigureAwait(false);
#endif
    }

    private void ReleaseBuffers()
    {
        try
        {
            _encoder.Dispose();
            _packed.Dispose();
            _framed.Dispose();
            if (_chunkInput is not null)
            {
                ArrayPool<byte>.Shared.Return(_chunkInput);
                _chunkInput = null;
            }
        }
        finally
        {
            _bufferPool.Return(_buffer);
        }
    }

    private void EncodeBuffered(bool final, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_chunkInput is null)
        {
            if (_bufferPosition == 0)
            {
                return;
            }
            _chunkInput = ArrayPool<byte>.Shared.Rent(MAX_UNCOMPRESSED_CHUNK_SIZE);
            _encoder.StartLzma2(_packed, _chunkInput, CompleteChunk);
        }
        using var input = new MemoryStream(_buffer, 0, _bufferPosition, writable: false);
        _encoder.Code(input, final, cancellationToken);
        _bufferPosition = 0;
        _framed.Position = 0;
    }

    private void FlushChunk(bool final = false)
    {
        try
        {
            EncodeBuffered(final, CancellationToken.None);
            _framed.WriteTo(_output);
            _framed.SetLength(0);
            _framed.Position = 0;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask FlushChunkAsync(
        bool final = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            EncodeBuffered(final, cancellationToken);
            await _framed.CopyToAsync(_output, 65536, cancellationToken).ConfigureAwait(false);
            _framed.SetLength(0);
            _framed.Position = 0;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private bool CompleteChunk(int unpackedSize)
    {
        var stored = _packed.Length + 2 >= unpackedSize;
        if (stored)
        {
            for (var offset = 0; offset < unpackedSize; )
            {
                var length = Math.Min(65536, unpackedSize - offset);
                _framed.WriteByte(_isFirstChunk ? (byte)1 : (byte)2);
                WriteSize(length);
                _framed.Write(_chunkInput!, offset, length);
                offset += length;
                _isFirstChunk = false;
            }
            _needsStateReset = true;
        }
        else
        {
            var control =
                _isFirstChunk ? 0xE0
                : _needsProperties ? 0xC0
                : _needsStateReset ? 0xA0
                : 0x80;
            _framed.WriteByte((byte)(control | ((unpackedSize - 1) >> 16)));
            WriteSize(unpackedSize);
            WriteSize(checked((int)_packed.Length));
            // A stream beginning with stored data has not sent LZMA properties yet.
            // Its first compressed chunk must use C0; subsequent stored-to-LZMA
            // transitions use A0, preserving the dictionary in both cases.
            if (_needsProperties)
            {
                _framed.WriteByte(_lzmaPropertiesByte);
            }
            _packed.WriteTo(_framed);
            _needsProperties = false;
            _needsStateReset = false;
            _isFirstChunk = false;
        }
        _packed.SetLength(0);
        _packed.Position = 0;
        return stored;
    }

    private void WriteSize(int size)
    {
        _framed.WriteByte((byte)((size - 1) >> 8));
        _framed.WriteByte((byte)(size - 1));
    }

    internal static byte EncodeDictionarySize(int dictSize)
    {
        // Special case: very small dictionary sizes
        if (dictSize <= (2 << 11))
        {
            return 0;
        }

        for (byte p = 0; p < 40; p++)
        {
            var shift = (p >> 1) + 11;
            if (shift >= 31)
            {
                return p;
            }

            var size = (2 | (p & 1)) << shift;
            if (size >= dictSize)
            {
                return p;
            }
        }

        return 40;
    }
}
