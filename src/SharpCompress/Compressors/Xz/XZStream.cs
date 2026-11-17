#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Compressors.Xz;

[CLSCompliant(false)]
public sealed partial class XZStream : XZReadOnlyStream
{
    public XZStream(Stream baseStream)
        : base(baseStream) { }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            _currentBlock?.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool _isDisposed;

    public override bool CanRead => !_isDisposed && base.CanRead;

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(XZStream));
        }
    }

    public static bool IsXZStream(Stream stream)
    {
        try
        {
            return null != XZHeader.FromStream(stream);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AssertBlockCheckTypeIsSupported()
    {
        switch (Header.BlockCheckType)
        {
            case CheckType.NONE:
            case CheckType.CRC32:
            case CheckType.CRC64:
            case CheckType.SHA256:
                break;
            default:
                throw new InvalidFormatException("Check Type unknown to this version of decoder.");
        }
    }

    public XZHeader Header { get; private set; }
    public XZIndex Index { get; private set; }
    public XZFooter Footer { get; private set; }
    public bool HeaderIsRead { get; private set; }
    private XZBlock _currentBlock;

    private bool _endOfStream;
    private readonly List<(ulong Unpadded, ulong Uncompressed)> _decodedBlocks = new();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        var bytesRead = 0;
        while (bytesRead < count && !_endOfStream)
        {
            if (!HeaderIsRead)
            {
                ReadHeader();
            }
            var read = ReadBlocks(buffer, offset + bytesRead, count - bytesRead);
            bytesRead += read;
            if (bytesRead < count)
            {
                ReadIndex();
                ReadFooter();
                ValidateStream();
                ReadNextStream();
            }
        }
        return bytesRead;
    }

    private void ReadNextStream()
    {
        var padding = 0;
        int value;
        while ((value = BaseStream.ReadByte()) == 0)
        {
            padding = (padding + 1) % 4;
        }
        if (padding != 0)
        {
            throw new InvalidFormatException("XZ stream padding is not a multiple of four.");
        }
        if (value == -1)
        {
            _endOfStream = true;
            return;
        }
        var header = new byte[12];
        header[0] = (byte)value;
        if (!BaseStream.ReadFully(header.AsSpan(1, 11)))
        {
            throw new InvalidFormatException("Incomplete concatenated XZ header.");
        }
        using var stream = new MemoryStream(header);
        Header = XZHeader.FromStream(stream);
        ResetStream();
    }

    private void ResetStream()
    {
        AssertBlockCheckTypeIsSupported();
        _currentBlock?.Dispose();
        _currentBlock = null;
        _decodedBlocks.Clear();
    }

    private void ValidateStream()
    {
        if (Index.Records.Count != _decodedBlocks.Count)
        {
            throw new InvalidFormatException("XZ index block count mismatch.");
        }
        for (var i = 0; i < _decodedBlocks.Count; i++)
        {
            if (
                Index.Records[i].UnpaddedSize != _decodedBlocks[i].Unpadded
                || Index.Records[i].UncompressedSize != _decodedBlocks[i].Uncompressed
            )
            {
                throw new InvalidFormatException("XZ index block sizes mismatch.");
            }
        }
        if (
            Footer.BackwardSize != Footer.StreamStartPosition - Index.StreamStartPosition
            || Footer.StreamFlags.Length != 2
            || Footer.StreamFlags[0] != 0
            || Footer.StreamFlags[1] != (byte)Header.BlockCheckType
        )
        {
            throw new InvalidFormatException("XZ footer does not match the stream header/index.");
        }
    }

    private void ReadHeader()
    {
        Header = XZHeader.FromStream(BaseStream);
        AssertBlockCheckTypeIsSupported();
        HeaderIsRead = true;
    }

    private void ReadIndex() => Index = XZIndex.FromStream(BaseStream, true);

    private void ReadFooter() => Footer = XZFooter.FromStream(BaseStream);

    private int ReadBlocks(byte[] buffer, int offset, int count)
    {
        var bytesRead = 0;
        if (_currentBlock is null)
        {
            NextBlock();
        }

        for (; ; )
        {
            try
            {
                if (bytesRead >= count)
                {
                    break;
                }

                var remaining = count - bytesRead;
                var newOffset = offset + bytesRead;
                var justRead = _currentBlock.Read(buffer, newOffset, remaining);
                if (justRead < remaining)
                {
                    NextBlock();
                }

                bytesRead += justRead;
            }
            catch (XZIndexMarkerReachedException)
            {
                break;
            }
        }
        return bytesRead;
    }

    private void NextBlock()
    {
        if (_currentBlock is not null)
        {
            _decodedBlocks.Add(
                (_currentBlock.DecodedUnpaddedSize, _currentBlock.DecodedUncompressedSize)
            );
            _currentBlock.Dispose();
        }
        _currentBlock = new XZBlock(BaseStream, Header.BlockCheckType, Header.BlockCheckSize);
    }
}
