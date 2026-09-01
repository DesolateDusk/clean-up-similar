using FileComparer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class WindowsRecycleBinServiceTests
{
    [Fact]
    public async Task MissingFileReturnsFailureWithoutPermanentDeleteFallback()
    {
        var service = new WindowsRecycleBinService(
            NullLogger<WindowsRecycleBinService>.Instance);
        const string path = "C:\\missing\\never-delete.bin";

        var result = await service.RecycleAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(path, result.Path);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task PreCancelledOperationDoesNotTouchFileSystem()
    {
        var service = new WindowsRecycleBinService(
            NullLogger<WindowsRecycleBinService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RecycleAsync("unused.bin", cancellation.Token));
    }
}
