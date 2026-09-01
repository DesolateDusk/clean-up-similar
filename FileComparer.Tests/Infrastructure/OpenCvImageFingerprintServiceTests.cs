using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Imaging;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class OpenCvImageFingerprintServiceTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    public async Task DecodesEverySupportedV1Format(string extension)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath($"supported{extension}");
        if (string.Equals(extension, ".gif", StringComparison.Ordinal))
        {
            await File.WriteAllBytesAsync(
                path,
                Convert.FromBase64String(
                    "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="),
                TestContext.Current.CancellationToken);
        }
        else
        {
            CreateTestImage(path, 80, 60);
        }

        var result = await CreateService(new StubCacheRepository())
            .GetFingerprintAsync(
                Entry(path),
                forceRefresh: false,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value?.PerceptualHash);
    }

    [Fact]
    public async Task ComputesAndCachesFingerprintFromPng()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("sample.png");
        CreateTestImage(path, 80, 60);
        var entry = Entry(path);
        var cache = new StubCacheRepository();
        var service = CreateService(cache);

        var result = await service.GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(80, result.Value.Width);
        Assert.Equal(60, result.Value.Height);
        Assert.Equal(4d / 3d, result.Value.AspectRatio, precision: 10);
        Assert.NotNull(result.Value.PerceptualHash);
        Assert.NotEmpty(result.Value.CandidateHashes);
        Assert.Empty(result.Issues);
        var cached = Assert.Single(cache.Records);
        Assert.Equal(16, cached.PerceptualHash?.Length);
        Assert.False(string.IsNullOrWhiteSpace(cached.ImageCandidateHashes));
        Assert.Equal(80, cached.Width);
        Assert.Equal(60, cached.Height);
    }

    [Fact]
    public async Task ReturnsMatchingCachedFingerprintWithoutReadingFile()
    {
        var entry = new FileEntry(
            "C:\\missing\\cached.png",
            "cached.png",
            ".png",
            123,
            new DateTime(638900000000000000, DateTimeKind.Utc));
        var cache = new StubCacheRepository();
        cache.Add(FileCacheRecord.Create(entry, DateTime.UtcNow) with
        {
            Width = 640,
            Height = 480,
            PerceptualHash = "0123456789ABCDEF",
            ImageCandidateHashes = "0123456789ABCDEF"
        });
        var service = CreateService(cache);

        var result = await service.GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(0x0123456789ABCDEFUL, result.Value.PerceptualHash);
        Assert.Equal(640, result.Value.Width);
        Assert.Equal(480, result.Value.Height);
        Assert.Equal(1, cache.GetCalls);
        Assert.Equal(0, cache.UpsertCalls);
    }

    [Fact]
    public async Task ForceRefreshBypassesExistingCache()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("refresh.png");
        CreateTestImage(path, 48, 32);
        var entry = Entry(path);
        var cache = new StubCacheRepository();
        cache.Add(FileCacheRecord.Create(entry, DateTime.UtcNow) with
        {
            Width = 1,
            Height = 1,
            PerceptualHash = "0000000000000000"
        });
        var service = CreateService(cache);

        var result = await service.GetFingerprintAsync(
            entry,
            forceRefresh: true,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(48, result.Value.Width);
        Assert.Equal(32, result.Value.Height);
        Assert.Equal(0, cache.GetCalls);
        Assert.Equal(1, cache.UpsertCalls);
    }

    [Fact]
    public async Task CacheFailureIsReportedButDoesNotStopFingerprinting()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("cache-failure.png");
        CreateTestImage(path, 40, 30);
        var entry = Entry(path);
        var cache = new StubCacheRepository { ThrowOnGet = true };
        var service = CreateService(cache);

        var result = await service.GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.Cache);
    }

    [Fact]
    public async Task ReportsInvalidImageWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("broken.jpg");
        await File.WriteAllBytesAsync(
            path,
            [0xFF, 0xD8, 0x00, 0x01],
            TestContext.Current.CancellationToken);
        var cache = new StubCacheRepository();
        var service = CreateService(cache);

        var result = await service.GetFingerprintAsync(
            Entry(path),
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.InvalidImage);
        Assert.Equal(0, cache.UpsertCalls);
    }

    private static OpenCvImageFingerprintService CreateService(
        StubCacheRepository cache) =>
        new(
            cache,
            TimeProvider.System,
            NullLogger<OpenCvImageFingerprintService>.Instance);

    private static void CreateTestImage(string path, int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(
            image,
            new Rect(width / 5, height / 5, width / 2, height / 2),
            new Scalar(20, 180, 240),
            thickness: -1);
        Assert.True(Cv2.ImWrite(path, image));
    }

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
