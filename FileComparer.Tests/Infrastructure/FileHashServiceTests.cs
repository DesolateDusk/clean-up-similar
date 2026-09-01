using System.Security.Cryptography;
using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Hashing;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class FileHashServiceTests
{
    [Fact]
    public async Task UsesCachedQuickHashUnlessForceRefreshIsRequested()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("file.bin");
        await File.WriteAllTextAsync(
            path,
            "new content",
            TestContext.Current.CancellationToken);
        var entry = Entry(path);
        var cache = new StubCacheRepository();
        cache.Add(FileCacheRecord.Create(
            entry,
            DateTime.UnixEpoch) with
        {
            QuickHash = "CACHED"
        });
        var service = CreateService(cache);

        var cached = await service.GetQuickHashAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);
        var refreshed = await service.GetQuickHashAsync(
            entry,
            forceRefresh: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("CACHED", cached.Value);
        Assert.NotEqual("CACHED", refreshed.Value);
        Assert.Equal(1, cache.GetCalls);
        Assert.Equal(1, cache.UpsertCalls);
    }

    [Fact]
    public async Task FullHashMatchesFrameworkSha256ForLargeFile()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("large.bin");
        var content = Enumerable.Range(0, 2 * 1024 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(
            path,
            content,
            TestContext.Current.CancellationToken);
        var entry = Entry(path);
        var service = CreateService(new StubCacheRepository());
        var expected = Convert.ToHexString(SHA256.HashData(content));

        var result = await service.GetSha256Async(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Value);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task RejectsEntryWhoseIdentityChangedBeforeHashing()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("changing.bin");
        await File.WriteAllTextAsync(
            path,
            "first",
            TestContext.Current.CancellationToken);
        var staleEntry = Entry(path);
        await File.WriteAllTextAsync(
            path,
            "a different length",
            TestContext.Current.CancellationToken);
        var cache = new StubCacheRepository();
        var service = CreateService(cache);

        var result = await service.GetSha256Async(
            staleEntry,
            forceRefresh: true,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Equal(
            ScanIssueCategory.ChangedDuringScan,
            Assert.Single(result.Issues).Category);
        Assert.Equal(0, cache.UpsertCalls);
    }

    [Fact]
    public async Task CacheFailureAddsIssueAndFallsBackToComputation()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("file.bin");
        await File.WriteAllTextAsync(
            path,
            "content",
            TestContext.Current.CancellationToken);
        var cache = new StubCacheRepository
        {
            ThrowOnGet = true,
            ThrowOnUpsert = true
        };
        var service = CreateService(cache);

        var result = await service.GetQuickHashAsync(
            Entry(path),
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Value);
        Assert.Equal(
            2,
            result.Issues.Count(
                issue => issue.Category == ScanIssueCategory.Cache));
    }

    private static FileHashService CreateService(
        StubCacheRepository cache) =>
        new(
            cache,
            TimeProvider.System,
            NullLogger<FileHashService>.Instance);

    private static FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry(
            info.FullName,
            info.Name,
            info.Extension,
            info.Length,
            info.LastWriteTimeUtc);
    }
}
