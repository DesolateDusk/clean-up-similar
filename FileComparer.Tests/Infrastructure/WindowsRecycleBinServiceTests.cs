using FileComparer.Infrastructure.FileSystem;
using FileComparer.Domain.Models;
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
            Entry(path),
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
            service.RecycleAsync(Entry("unused.bin"), cancellation.Token));
    }

    [Fact]
    public async Task ChangedFileIsNotRecycled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"file-comparer-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(
                path,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            var entry = Entry(path) with { Size = 2 };
            var service = new WindowsRecycleBinService(
                NullLogger<WindowsRecycleBinService>.Instance);

            var result = await service.RecycleAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.True(File.Exists(path));
            Assert.Contains("已變更", result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FileEntry Entry(string path) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), 0, DateTime.UtcNow);
}
