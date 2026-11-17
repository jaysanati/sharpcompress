namespace SharpCompress.Common;

/// <summary>
/// The decompressed entry data does not match its stored checksum.
/// </summary>
public class ChecksumMismatchException : InvalidFormatException
{
    public ChecksumMismatchException(string message)
        : base(message) { }
}
