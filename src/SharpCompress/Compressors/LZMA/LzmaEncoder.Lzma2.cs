using System;
using System.IO;

namespace SharpCompress.Compressors.LZMA;

internal partial class Encoder
{
    private byte[]? _lzma2Input;
    private Stream? _lzma2Output;
    private Func<int, bool>? _lzma2ChunkCompleted;
    private int _lzma2InputSize;

    internal void StartLzma2(Stream packed, byte[] input, Func<int, bool> chunkCompleted)
    {
        SetStreams(null!, packed, -1, -1);
        _lzma2Input = input;
        _lzma2Output = packed;
        _lzma2ChunkCompleted = chunkCompleted;
    }

    private void CaptureLzma2Input(uint length)
    {
        if (_lzma2Input is null)
        {
            return;
        }
        // The match finder may have advanced during optimum parsing. Copy the actual
        // symbol before its window can slide, so fallback never re-reads the source.
        for (var i = 0; i < length; i++)
        {
            _lzma2Input[_lzma2InputSize++] = _matchFinder.GetIndexByte(
                (int)(i - _additionalOffset)
            );
        }
    }

    private void CheckLzma2Chunk()
    {
        if (_lzma2Output is null || _additionalOffset != 0)
        {
            return;
        }
        // Like LzmaEnc_CodeOneMemBlock, finish only when the optimum path has been
        // consumed. Reserve its maximum look-ahead, a match, and worst-case coding
        // space, including delayed range-coder carry bytes and its flush.
        if (
            _lzma2InputSize + K_NUM_OPTS + 300 >= 1 << 21
            || _lzma2Output.Position + _rangeEncoder.PendingSize + K_NUM_OPTS * 8 >= 1 << 16
        )
        {
            CompleteLzma2Chunk();
        }
    }

    private void CompleteLzma2Chunk()
    {
        if (_lzma2InputSize == 0)
        {
            return;
        }
        _rangeEncoder.FlushData();
        var resetState = _lzma2ChunkCompleted!(_lzma2InputSize);
        _lzma2InputSize = 0;
        if (resetState)
        {
            // Stored bytes advance the dictionary and absolute position, but the
            // decoder did not see their trial probabilities, state or rep distances.
            var previousByte = _previousByte;
            Init();
            _previousByte = previousByte;
            FillDistancesPrices();
            FillAlignPrices();
            _lenEncoder.UpdateTables((uint)1 << _posStateBits);
            _repMatchLenEncoder.UpdateTables((uint)1 << _posStateBits);
        }
        else
        {
            _rangeEncoder.Init();
        }
    }
}
