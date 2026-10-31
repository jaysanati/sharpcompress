using System;
using System.IO;

namespace SharpCompress.Compressors.LZMA.LZ;

internal interface IMatchFinder : IDisposable
{
    void Create(
        uint historySize,
        uint keepAddBufferBefore,
        uint matchMaxLen,
        uint keepAddBufferAfter
    );
    void SetStream(Stream stream);
    void ReleaseStream();
    void Init();
    byte GetIndexByte(int index);
    uint GetMatchLen(int index, uint distance, uint limit);
    uint GetNumAvailableBytes();
    uint GetMatches(uint[] distances);
    void Skip(uint num);
    void SetCutValue(uint cutValue);
    bool IsDataStarved { get; }
}
