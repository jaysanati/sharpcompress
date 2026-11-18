namespace SharpCompress.Common;

/// <summary>
/// A dictionary, window, or model size exceeds its configured limit.
/// </summary>
public sealed class DictionarySizeLimitExceededException : InvalidFormatException
{
    public DictionarySizeLimitExceededException(string format, long requestedSize, long maximumSize)
        : base(
            $"{format} dictionary size {requestedSize} is invalid or exceeds the maximum of {maximumSize} bytes."
        )
    {
        Format = format;
        RequestedSize = requestedSize;
        MaximumSize = maximumSize;
    }

    public string Format { get; }

    public long RequestedSize { get; }

    public long MaximumSize { get; }
}
