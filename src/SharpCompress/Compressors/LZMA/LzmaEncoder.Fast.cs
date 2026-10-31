namespace SharpCompress.Compressors.LZMA;

internal partial class Encoder
{
    // Port of GetOptimumFast from the public-domain LZMA SDK 24.09, C/LzmaEnc.c.
    // In this encoder rep distances, like match-finder distances, are zero based.
    private uint GetOptimumFast(out uint backRes)
    {
        uint mainLen;
        uint numPairs;
        if (_additionalOffset == 0)
        {
            ReadMatchDistances(out mainLen, out numPairs);
        }
        else
        {
            // A literal chosen by look-ahead leaves the next position already searched.
            mainLen = _longestMatchLength;
            numPairs = _numDistancePairs;
        }
        backRes = uint.MaxValue;
        var available = System.Math.Min(
            _matchFinder.GetNumAvailableBytes() + 1,
            Base.K_MATCH_MAX_LEN
        );
        if (available < 2)
        {
            return 1;
        }

        uint repLen = 0;
        uint repIndex = 0;
        for (uint i = 0; i < Base.K_NUM_REP_DISTANCES; i++)
        {
            var length = _matchFinder.GetMatchLen(-1, _repDistances[i], available);
            if (length < 2)
            {
                continue;
            }
            if (length >= _numFastBytes)
            {
                backRes = i;
                MovePos(length - 1);
                return length;
            }
            if (length > repLen)
            {
                repLen = length;
                repIndex = i;
            }
        }
        if (mainLen >= _numFastBytes)
        {
            backRes = _matchDistances[numPairs - 1] + Base.K_NUM_REP_DISTANCES;
            MovePos(mainLen - 1);
            return mainLen;
        }
        uint mainDist = 0;
        if (mainLen >= 2)
        {
            mainDist = _matchDistances[numPairs - 1];
            while (numPairs > 2 && mainLen == _matchDistances[numPairs - 4] + 1)
            {
                var distance = _matchDistances[numPairs - 3];
                if (!ChangePair(distance, mainDist))
                {
                    break;
                }
                numPairs -= 2;
                mainLen--;
                mainDist = distance;
            }
            if (mainLen == 2 && mainDist >= 0x80)
            {
                mainLen = 1;
            }
        }
        if (
            repLen >= 2
            && (
                repLen + 1 >= mainLen
                || (repLen + 2 >= mainLen && mainDist >= (1 << 9))
                || (repLen + 3 >= mainLen && mainDist >= (1 << 15))
            )
        )
        {
            backRes = repIndex;
            MovePos(repLen - 1);
            return repLen;
        }
        if (mainLen < 2 || available <= 2)
        {
            return 1;
        }

        ReadMatchDistances(out _longestMatchLength, out _numDistancePairs);
        var nextLen = _longestMatchLength;
        if (nextLen >= 2)
        {
            var nextDist = _matchDistances[_numDistancePairs - 1];
            if (
                (nextLen >= mainLen && nextDist < mainDist)
                || (nextLen == mainLen + 1 && !ChangePair(mainDist, nextDist))
                || nextLen > mainLen + 1
                || (nextLen + 1 >= mainLen && mainLen >= 3 && ChangePair(nextDist, mainDist))
            )
            {
                return 1;
            }
        }
        for (uint i = 0; i < Base.K_NUM_REP_DISTANCES; i++)
        {
            var length = _matchFinder.GetMatchLen(
                -1,
                _repDistances[i],
                System.Math.Max(2, mainLen - 1)
            );
            if (length >= 2 && length >= mainLen - 1)
            {
                return 1;
            }
        }
        backRes = mainDist + Base.K_NUM_REP_DISTANCES;
        MovePos(mainLen - 2);
        return mainLen;
    }

    private static bool ChangePair(uint smallDistance, uint bigDistance) =>
        (bigDistance >> 7) > smallDistance;
}
