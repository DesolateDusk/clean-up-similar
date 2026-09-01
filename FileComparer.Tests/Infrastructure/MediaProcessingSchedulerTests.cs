using FileComparer.Infrastructure.Media;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class MediaProcessingSchedulerTests
{
    [Fact]
    public async Task AllowsOnlyTwoMediaOperationsToRunAtOnce()
    {
        using var scheduler = new MediaProcessingScheduler();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;

        async Task<int> Operation(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref running);
            if (current == 2)
            {
                twoStarted.TrySetResult(true);
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return current;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var operations = Enumerable.Range(0, 3)
            .Select(_ => scheduler.RunAsync(
                Operation,
                TestContext.Current.CancellationToken))
            .ToArray();

        try
        {
            await twoStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, Volatile.Read(ref running));
        }
        finally
        {
            release.TrySetResult(true);
        }

        await Task.WhenAll(operations);
        Assert.Equal(0, Volatile.Read(ref running));
    }

    [Fact]
    public async Task ReleasesThePermitAfterAnOperationIsCancelled()
    {
        using var scheduler = new MediaProcessingScheduler();
        var cancelledOperation = scheduler.RunAsync(
            _ => Task.FromCanceled<int>(new CancellationToken(canceled: true)),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledOperation);

        var result = await scheduler.RunAsync(
            _ => Task.FromResult(42),
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }
}
