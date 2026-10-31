using System.Threading.Tasks;

namespace SharpCompress.Common.SevenZip;

internal static class SevenZipPipelineWait
{
    // Only the synchronous writer and CPU workers use these waits. Pipeline awaits
    // never capture a caller synchronization context. Convert ValueTask to Task so
    // the wait is valid even if a future implementation uses IValueTaskSource.
#pragma warning disable VSTHRD002
    internal static void WaitForCompletion(this ValueTask task) =>
        task.AsTask().GetAwaiter().GetResult();

    internal static void WaitForCompletion(this Task task) => task.GetAwaiter().GetResult();

    internal static T WaitForCompletion<T>(this Task<T> task) => task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
}
