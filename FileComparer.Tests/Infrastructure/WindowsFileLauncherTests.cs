using FileComparer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class WindowsFileLauncherTests
{
    [Fact]
    public async Task OpenFileReportsMissingPathWithoutStartingShell()
    {
        var launcher = new WindowsFileLauncher(
            NullLogger<WindowsFileLauncher>.Instance);

        var result = await launcher.OpenFileAsync(
            "C:\\missing\\file.jpg",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task OpenContainingFolderReportsMissingDirectory()
    {
        var launcher = new WindowsFileLauncher(
            NullLogger<WindowsFileLauncher>.Instance);

        var result = await launcher.OpenContainingFolderAsync(
            "C:\\missing\\file.jpg",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }
}
