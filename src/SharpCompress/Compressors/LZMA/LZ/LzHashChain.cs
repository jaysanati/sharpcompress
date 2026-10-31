#nullable disable

using System;
using System.Buffers;
using System.IO;

namespace SharpCompress.Compressors.LZMA.LZ;

// Port of HC4 from the public-domain LZMA SDK 24.09, C/LzFind.c:
// Hc4_MatchFinder_GetMatches, Hc_GetMatchesSpec and Hc4_MatchFinder_Skip.
internal sealed class HashChain : InWindow, IMatchFinder
{
    private readonly ArrayPool<uint> _pool;

    public HashChain()
        : this(ArrayPool<uint>.Shared) { }

    internal HashChain(ArrayPool<uint> pool) => _pool = pool;

    private uint _cyclicBufferPos;
    private uint _cyclicBufferSize;
    private uint _matchMaxLen;

    private uint[] _son = [];
    private uint[] _hash = [];

    private uint _cutValue;
    private uint? _explicitCutValue;
    private uint _hashMask;
    private uint _hashSizeSum;

    private const uint K_HASH2_SIZE = 1 << 10;
    private const uint K_HASH3_SIZE = 1 << 16;
    private const uint K_FIX_HASH_SIZE = K_HASH2_SIZE + K_HASH3_SIZE;
    private const uint K_EMPTY_HASH_VALUE = 0;
    private const uint K_MAX_VAL_FOR_NORMALIZE = ((uint)1 << 31) - 1;

    public new void SetStream(Stream stream) => base.SetStream(stream);

    public new void ReleaseStream() => base.ReleaseStream();

    public new void Init()
    {
        base.Init();
        for (uint i = 0; i < _hashSizeSum; i++)
        {
            _hash[i] = K_EMPTY_HASH_VALUE;
        }
        _cyclicBufferPos = 0;
        ReduceOffsets(-1);
    }

    public new void MovePos()
    {
        if (++_cyclicBufferPos >= _cyclicBufferSize)
        {
            _cyclicBufferPos = 0;
        }
        base.MovePos();
        if (_pos == K_MAX_VAL_FOR_NORMALIZE)
        {
            Normalize();
        }
    }

    public new byte GetIndexByte(int index) => base.GetIndexByte(index);

    public new uint GetMatchLen(int index, uint distance, uint limit) =>
        base.GetMatchLen(index, distance, limit);

    public new uint GetNumAvailableBytes() => base.GetNumAvailableBytes();

    public void Create(
        uint historySize,
        uint keepAddBufferBefore,
        uint matchMaxLen,
        uint keepAddBufferAfter
    )
    {
        ThrowHelper.ThrowIfGreaterThan(historySize, K_MAX_VAL_FOR_NORMALIZE - 256);
        _cutValue = _explicitCutValue ?? ((16 + (matchMaxLen >> 1)) >> 1);

        var windowReservSize =
            ((historySize + keepAddBufferBefore + matchMaxLen + keepAddBufferAfter) / 2) + 256;

        Create(
            historySize + keepAddBufferBefore,
            matchMaxLen + keepAddBufferAfter,
            windowReservSize
        );

        _matchMaxLen = matchMaxLen;

        var cyclicBufferSize = historySize + 1;
        if (_cyclicBufferSize != cyclicBufferSize)
        {
            var replacement = _pool.Rent(checked((int)(cyclicBufferSize)));
            if (_son.Length != 0)
            {
                _pool.Return(_son);
            }
            _cyclicBufferSize = cyclicBufferSize;
            _son = replacement;
        }

        var hs = historySize - 1;
        hs |= hs >> 1;
        hs |= hs >> 2;
        hs |= hs >> 4;
        hs |= hs >> 8;
        hs >>= 1;
        hs |= 0xFFFF;
        if (hs > (1 << 24))
        {
            hs >>= 1;
        }
        _hashMask = hs;
        hs += 1 + K_FIX_HASH_SIZE;
        if (hs != _hashSizeSum)
        {
            var replacement = _pool.Rent(checked((int)hs));
            if (_hash.Length != 0)
            {
                _pool.Return(_hash);
            }
            _hashSizeSum = hs;
            _hash = replacement;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        if (_son.Length != 0)
        {
            _pool.Return(_son);
            _son = [];
        }
        if (_hash.Length != 0)
        {
            _pool.Return(_hash);
            _hash = [];
        }
    }

    private uint UpdateHashes(out uint distance2, out uint distance3)
    {
        var cur = _bufferOffset + _pos;
        var temp = Crc.TABLE[_bufferBase[cur]] ^ _bufferBase[cur + 1];
        var hash2 = temp & (K_HASH2_SIZE - 1);
        temp ^= (uint)_bufferBase[cur + 2] << 8;
        var hash3 = temp & (K_HASH3_SIZE - 1);
        var hash4 = (temp ^ (Crc.TABLE[_bufferBase[cur + 3]] << 5)) & _hashMask;
        distance2 = _pos - _hash[hash2];
        distance3 = _pos - _hash[K_HASH2_SIZE + hash3];
        var match = _hash[K_FIX_HASH_SIZE + hash4];
        _hash[hash2] = _pos;
        _hash[K_HASH2_SIZE + hash3] = _pos;
        _hash[K_FIX_HASH_SIZE + hash4] = _pos;
        _son[_cyclicBufferPos] = match;
        return match;
    }

    public uint GetMatches(uint[] distances)
    {
        var limit = Math.Min(_matchMaxLen, GetNumAvailableBytes());
        if (limit < 4)
        {
            MovePos();
            return 0;
        }
        var cur = _bufferOffset + _pos;
        var match = UpdateHashes(out var distance2, out var distance3);
        var minimum = Math.Min(_pos, _cyclicBufferSize);
        uint offset = 0;
        uint maxLen = 3;
        // The short hashes guarantee the intervening bytes once byte zero matches.
        if (distance2 < minimum && _bufferBase[cur - distance2] == _bufferBase[cur])
        {
            distances[offset++] = 2;
            distances[offset++] = distance2 - 1;
            if (_bufferBase[cur - distance2 + 2] == _bufferBase[cur + 2])
            {
                maxLen = ExtendShortMatch(distance2);
            }
            else if (distance3 < minimum && _bufferBase[cur - distance3] == _bufferBase[cur])
            {
                distances[offset++] = 3;
                distances[offset++] = distance3 - 1;
                maxLen = ExtendShortMatch(distance3);
            }
        }
        else if (distance3 < minimum && _bufferBase[cur - distance3] == _bufferBase[cur])
        {
            distances[offset++] = 3;
            distances[offset++] = distance3 - 1;
            maxLen = ExtendShortMatch(distance3);
        }
        if (maxLen < limit)
        {
            var count = _cutValue;
            while (match != 0 && count-- != 0)
            {
                var delta = _pos - match;
                if (delta >= _cyclicBufferSize)
                {
                    break;
                }
                var cyclic =
                    delta <= _cyclicBufferPos
                        ? _cyclicBufferPos - delta
                        : _cyclicBufferPos - delta + _cyclicBufferSize;
                match = _son[cyclic];
                if (_bufferBase[cur - delta + maxLen] != _bufferBase[cur + maxLen])
                {
                    continue;
                }
                uint length = 0;
                while (
                    length < limit && _bufferBase[cur - delta + length] == _bufferBase[cur + length]
                )
                {
                    length++;
                }
                if (length > maxLen)
                {
                    distances[offset++] = maxLen = length;
                    distances[offset++] = delta - 1;
                    if (length == limit)
                    {
                        break;
                    }
                }
            }
        }
        MovePos();
        return offset;

        uint ExtendShortMatch(uint distance)
        {
            uint length = 3;
            while (
                length < limit && _bufferBase[cur - distance + length] == _bufferBase[cur + length]
            )
            {
                length++;
            }
            distances[offset - 2] = length;
            return length;
        }
    }

    public void Skip(uint num)
    {
        while (num-- != 0)
        {
            if (GetNumAvailableBytes() >= 4)
            {
                UpdateHashes(out _, out _);
            }
            MovePos();
        }
    }

    private void NormalizeLinks(uint[] items, uint numItems, uint subValue)
    {
        for (uint i = 0; i < numItems; i++)
        {
            var value = items[i];
            if (value <= subValue)
            {
                value = K_EMPTY_HASH_VALUE;
            }
            else
            {
                value -= subValue;
            }
            items[i] = value;
        }
    }

    private void Normalize()
    {
        var subValue = _pos - _cyclicBufferSize;
        NormalizeLinks(_son, _cyclicBufferSize, subValue);
        NormalizeLinks(_hash, _hashSizeSum, subValue);
        ReduceOffsets((int)subValue);
    }

    public void SetCutValue(uint cutValue) => _cutValue = (_explicitCutValue = cutValue).Value;
}
