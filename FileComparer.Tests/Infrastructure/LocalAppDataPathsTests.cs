using FileComparer.Infrastructure.Persistence;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class LocalAppDataPathsTests
{
    [Fact]
    public void CreatesDedicatedCacheAndLogDirectories()
    {
        using var directory = new TemporaryDirectory();

        var paths = new LocalAppDataPaths(directory.FullPath);

        Assert.StartsWith(directory.FullPath, paths.CacheDatabasePath);
        Assert.Equal("file-cache.db", Path.GetFileName(paths.CacheDatabasePath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(
            paths.CacheDatabasePath)));
        Assert.True(Directory.Exists(paths.LogDirectoryPath));
        Assert.NotEqual(
            Path.GetDirectoryName(paths.CacheDatabasePath),
            paths.LogDirectoryPath);
    }
}
