using SharpCompress.Common;
using SharpCompress.Compressors.ZStandard.Unsafe;

namespace SharpCompress.Compressors.ZStandard;

public class ZstdException : SharpCompressException
{
    public ZstdException(ZSTD_ErrorCode code, string message)
        : base(message) => Code = code;

    internal ZstdException(
        ZSTD_ErrorCode code,
        string message,
        DictionarySizeLimitExceededException inner
    )
        : base(message, inner) => Code = code;

    public ZSTD_ErrorCode Code { get; }
}
