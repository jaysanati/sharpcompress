using System.Buffers;

namespace SharpCompress.Compressors.LZMA.LZ;

internal static class EncoderArrayPool
{
    // Large histories are often one element beyond a power of two. Exact allocations
    // keep commit charge within admission estimates and avoid retaining them in Shared.
    internal static T[] Rent<T>(ArrayPool<T> pool, int length, int elementSize) =>
        ReferenceEquals(pool, ArrayPool<T>.Shared) && (long)length * elementSize > 1048576
            ? new T[length]
            : pool.Rent(length);

    internal static void Return<T>(ArrayPool<T> pool, T[] array, int elementSize)
    {
        if (
            !ReferenceEquals(pool, ArrayPool<T>.Shared)
            || (long)array.Length * elementSize <= 1048576
        )
        {
            pool.Return(array);
        }
    }
}
