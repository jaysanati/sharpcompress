using System;
using System.IO;
using SharpCompress.IO;

namespace SharpCompress.Common.SevenZip;

/// <summary>Pooled job storage, spilling above 256 MiB (the largest LZMA2 block, so only unsplittable LZMA
/// folders spill). Admission charges at most this much per buffer.</summary>
internal sealed class SevenZipJobBuffer : Stream
{
    internal const long MemoryLimit = 256L << 20;
    private Stream storage = new PooledMemoryStream(0, 65536);
    public override bool CanRead => storage.CanRead;
    public override bool CanSeek => storage.CanSeek;
    public override bool CanWrite => storage.CanWrite;
    public override long Length => storage.Length;
    public override long Position
    {
        get => storage.Position;
        set => storage.Position = value;
    }

    public override void Flush() => storage.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        storage.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => storage.Seek(offset, origin);

    public override void SetLength(long value) => storage.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (storage is PooledMemoryStream && storage.Position + count > MemoryLimit)
        {
            // CreateNew and DeleteOnClose avoid name races and remove spills on every exit path.
            var path = Path.Combine(
                Path.GetTempPath(),
                "sharpcompress-7z-" + Guid.NewGuid().ToString("N")
            );
            var file = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                65536,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan
            );
            try
            {
                var position = storage.Position;
                storage.Position = 0;
                storage.CopyTo(file, 65536);
                file.Position = position;
            }
            catch
            {
                file.Dispose();
                throw;
            }
            storage.Dispose();
            storage = file;
        }
        storage.Write(buffer, offset, count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            storage.Dispose();
        }
        base.Dispose(disposing);
    }
}
