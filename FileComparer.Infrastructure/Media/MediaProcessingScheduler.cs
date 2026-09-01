using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Rules;

namespace FileComparer.Infrastructure.Media;

/// <summary>
/// Bounds native media work so concurrent scans cannot exhaust CPU, disk or
/// OpenCV native memory. The fixed degree is product policy, not a UI option.
/// </summary>
public sealed class MediaProcessingScheduler : IMediaProcessingScheduler,
    IDisposable
{
    private readonly SemaphoreSlim gate = new(
        MediaSimilarityPolicy.MaximumMediaProcessingConcurrency,
        MediaSimilarityPolicy.MaximumMediaProcessingConcurrency);
    private bool disposed;

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        gate.Dispose();
        disposed = true;
    }
}
