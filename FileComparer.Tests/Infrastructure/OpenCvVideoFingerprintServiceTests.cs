using FileComparer.Domain.Models;
using FileComparer.Infrastructure.Media;
using FileComparer.Infrastructure.Persistence;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class OpenCvVideoFingerprintServiceTests
{
    [Fact]
    public async Task ExtractsTwelveFramesAndReusesCachedFingerprint()
    {
        using var directory = new TemporaryDirectory();
        using var appData = new TemporaryDirectory();
        var path = directory.GetPath("sample.avi");
        CreateVideo(path, frameStart: 0, frameCount: 36);
        var entry = Entry(path);
        using var cache = CreateCache(appData);
        using var scheduler = new MediaProcessingScheduler();
        var service = CreateService(cache, scheduler);

        var first = await service.GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);
        File.Delete(path);
        var second = await service.GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);
        var forceRefreshed = await service.GetFingerprintAsync(
            entry,
            forceRefresh: true,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.Equal(12, first.Value?.Frames.Count);
        Assert.True(second.IsSuccess);
        Assert.Equal(
            first.Value?.Frames.Select(frame => frame.PerceptualHash),
            second.Value?.Frames.Select(frame => frame.PerceptualHash));
        Assert.All(second.Value!.Frames, frame => Assert.InRange(
            frame.PositionRatio,
            0,
            1));
        Assert.False(forceRefreshed.IsSuccess);
        Assert.Contains(
            forceRefreshed.Issues,
            issue => issue.Category == ScanIssueCategory.NotFound);
    }

    [Fact]
    public async Task DoesNotReuseCachedVideoWithAnOldFingerprintVersion()
    {
        using var directory = new TemporaryDirectory();
        using var appData = new TemporaryDirectory();
        var path = directory.GetPath("stale.avi");
        CreateVideo(path, frameStart: 0, frameCount: 12);
        var entry = Entry(path);
        using var cache = CreateCache(appData);
        await cache.UpsertVideoAsync(
            new VideoCacheRecord(
                entry.FullPath,
                entry.Size,
                entry.LastWriteTimeUtc.Ticks,
                DurationMilliseconds: 1_000,
                Width: 96,
                Height: 64,
                VideoCodec: "MJPG",
                FingerprintVersion: "video-frame-phash-v0",
                Frames:
                [
                    new VideoFrameCacheRecord(
                        SampleIndex: 0,
                        PositionRatio: 0,
                        TimestampMilliseconds: 0,
                        PerceptualHash: "0000000000000000",
                        Contrast: 20)
                ],
                UpdatedAtUtcTicks: DateTime.UtcNow.Ticks),
            TestContext.Current.CancellationToken);
        File.Delete(path);
        using var scheduler = new MediaProcessingScheduler();

        var result = await CreateService(cache, scheduler).GetFingerprintAsync(
            entry,
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.NotFound);
    }

    [Fact]
    public async Task HonorsCancellationBeforeOpeningTheVideo()
    {
        using var appData = new TemporaryDirectory();
        using var cache = CreateCache(appData);
        using var scheduler = new MediaProcessingScheduler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateService(cache, scheduler).GetFingerprintAsync(
                new FileEntry(
                    "C:\\not-opened.avi",
                    "not-opened.avi",
                    ".avi",
                    0,
                    DateTime.UnixEpoch),
                forceRefresh: false,
                cancellation.Token));
    }

    [Fact]
    public async Task ReportsInvalidVideoWithoutStoppingTheCaller()
    {
        using var directory = new TemporaryDirectory();
        using var appData = new TemporaryDirectory();
        var path = directory.GetPath("broken.avi");
        await File.WriteAllBytesAsync(
            path,
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        using var cache = CreateCache(appData);
        using var scheduler = new MediaProcessingScheduler();

        var result = await CreateService(cache, scheduler).GetFingerprintAsync(
            Entry(path),
            forceRefresh: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.InvalidVideo);
    }

    private static OpenCvVideoFingerprintService CreateService(
        SqliteCacheRepository cache,
        MediaProcessingScheduler scheduler) =>
        new(
            cache,
            scheduler,
            TimeProvider.System,
            NullLogger<OpenCvVideoFingerprintService>.Instance);

    private static SqliteCacheRepository CreateCache(TemporaryDirectory directory) =>
        new(
            new StubAppDataPaths(
                directory.GetPath("cache.db"),
                directory.FullPath),
            NullLogger<SqliteCacheRepository>.Instance);

    private static void CreateVideo(string path, int frameStart, int frameCount)
    {
        var size = new Size(96, 64);
        using var writer = new VideoWriter(
            path,
            VideoWriter.FourCC('M', 'J', 'P', 'G'),
            fps: 12,
            size,
            isColor: true);
        Assert.True(writer.IsOpened());
        using var frame = new Mat(size.Height, size.Width, MatType.CV_8UC3);
        for (var index = 0; index < frameCount; index++)
        {
            var value = frameStart + index;
            frame.SetTo(new Scalar(value * 3 % 255, 80, 220));
            Cv2.Circle(
                frame,
                new Point(8 + value % 72, 32),
                8,
                Scalar.White,
                thickness: -1);
            Cv2.Line(
                frame,
                new Point(0, value % size.Height),
                new Point(size.Width - 1, (value * 3) % size.Height),
                new Scalar(30, 200, 30),
                thickness: 2);
            writer.Write(frame);
        }
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
