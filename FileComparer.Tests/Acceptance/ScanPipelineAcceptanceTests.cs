using FileComparer.Application.Compare;
using FileComparer.Application.Scan;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Infrastructure.FileSystem;
using FileComparer.Infrastructure.Hashing;
using FileComparer.Infrastructure.Imaging;
using FileComparer.Infrastructure.Media;
using FileComparer.Infrastructure.Persistence;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace FileComparer.Tests.Acceptance;

public sealed class ScanPipelineAcceptanceTests
{
    [Fact]
    public async Task ExactPipelineFindsDuplicatesWithoutGroupingSameSizeDifference()
    {
        using var scanDirectory = new TemporaryDirectory();
        using var appDataDirectory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            scanDirectory.GetPath("duplicate-a.bin"),
            [1, 2, 3, 4, 5, 6],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            scanDirectory.GetPath("duplicate-b.bin"),
            [1, 2, 3, 4, 5, 6],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            scanDirectory.GetPath("same-size-different.bin"),
            [6, 5, 4, 3, 2, 1],
            TestContext.Current.CancellationToken);
        using var cache = CreateCache(appDataDirectory);
        await cache.InitializeAsync(TestContext.Current.CancellationToken);
        var hashes = new FileHashService(
            cache,
            TimeProvider.System,
            NullLogger<FileHashService>.Instance);
        var service = CreateScanService(
            new ExactFileCompareStrategy(hashes, hashes));

        var result = await service.ScanAsync(
            new ScanRequest(
                scanDirectory.FullPath,
                IncludeSubdirectories: false,
                CompareMode.ExactFiles),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanStatus.Completed, result.Status);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Count);
        Assert.DoesNotContain(
            group.Items,
            item => item.File.Name == "same-size-different.bin");
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task ImagePipelineGroupsResizedJpegAndContinuesPastBrokenImage()
    {
        using var scanDirectory = new TemporaryDirectory();
        using var appDataDirectory = new TemporaryDirectory();
        var originalPath = scanDirectory.GetPath("original.png");
        var resizedPath = scanDirectory.GetPath("resized.jpg");
        CreateSimilarImages(originalPath, resizedPath);
        await File.WriteAllBytesAsync(
            scanDirectory.GetPath("broken.jpg"),
            [0xFF, 0xD8, 0x00],
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            scanDirectory.GetPath("notes.txt"),
            "not an image",
            TestContext.Current.CancellationToken);
        using var cache = CreateCache(appDataDirectory);
        await cache.InitializeAsync(TestContext.Current.CancellationToken);
        var fingerprints = new OpenCvImageFingerprintService(
            cache,
            TimeProvider.System,
            NullLogger<OpenCvImageFingerprintService>.Instance);
        using var scheduler = new MediaProcessingScheduler();
        var imageStrategy = new ImageSimilarityCompareStrategy(
            fingerprints,
            new MultiBandImageCandidateIndex(),
            new OpenCvImageSimilarityVerifier(
                scheduler,
                NullLogger<OpenCvImageSimilarityVerifier>.Instance));
        var service = CreateScanService(
            new SimilarMediaCompareStrategy(
                imageStrategy,
                new VideoSimilarityCompareStrategy(
                    new NoVideoFingerprintService())));

        var result = await service.ScanAsync(
            new ScanRequest(
                scanDirectory.FullPath,
                IncludeSubdirectories: false,
                CompareMode.SimilarMedia),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanStatus.Completed, result.Status);
        var group = Assert.Single(result.Groups);
        Assert.Contains(
            group.Items,
            item => item.File.FullPath == originalPath);
        Assert.Contains(
            group.Items,
            item => item.File.FullPath == resizedPath);
        Assert.Contains(
            result.Issues,
            issue => issue.Category == ScanIssueCategory.InvalidImage &&
                     issue.Path?.EndsWith(
                         "broken.jpg",
                         StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(4, result.DiscoveredCount);
    }

    [Fact]
    public async Task VideoPipelineGroupsReencodedTrimmedContentAcrossSamplePositions()
    {
        using var scanDirectory = new TemporaryDirectory();
        using var appDataDirectory = new TemporaryDirectory();
        var originalPath = scanDirectory.GetPath("original.avi");
        var trimmedPath = scanDirectory.GetPath("trimmed-reencoded.avi");
        CreateVideo(originalPath, frameStart: 0, frameCount: 24);
        ReencodeVideoSegment(originalPath, trimmedPath, startFrame: 12);
        using var cache = CreateCache(appDataDirectory);
        await cache.InitializeAsync(TestContext.Current.CancellationToken);
        using var scheduler = new MediaProcessingScheduler();
        var fingerprints = new OpenCvVideoFingerprintService(
            cache,
            scheduler,
            TimeProvider.System,
            NullLogger<OpenCvVideoFingerprintService>.Instance);
        var strategy = new VideoSimilarityCompareStrategy(fingerprints);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                new ScanRequest(
                    scanDirectory.FullPath,
                    IncludeSubdirectories: false,
                    CompareMode.SimilarMedia),
                [Entry(originalPath), Entry(trimmedPath)]),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(CompareGroupType.SimilarVideo, group.Type);
        Assert.Equal(
            new[] { originalPath, trimmedPath },
            group.Items.Select(item => item.File.FullPath).Order());
        Assert.Contains(
            group.Items,
            item => item.SimilarityScore is > 0 and < 1);
    }

    private static ScanApplicationService CreateScanService(
        ICompareStrategy strategy) =>
        new(
            new DirectoryFileScanner(
                NullLogger<DirectoryFileScanner>.Instance),
            [strategy],
            NullLogger<ScanApplicationService>.Instance);

    private static SqliteCacheRepository CreateCache(
        TemporaryDirectory directory) =>
        new(
            new StubAppDataPaths(
                directory.GetPath("file-cache.db"),
                directory.FullPath),
            NullLogger<SqliteCacheRepository>.Instance);

    private static void CreateSimilarImages(
        string originalPath,
        string resizedPath)
    {
        using var original = new Mat(
            rows: 120,
            cols: 160,
            MatType.CV_8UC3,
            Scalar.Black);
        Cv2.Rectangle(
            original,
            new Rect(18, 20, 92, 62),
            new Scalar(40, 210, 245),
            thickness: -1);
        Cv2.Circle(
            original,
            new Point(125, 82),
            20,
            new Scalar(230, 80, 30),
            thickness: -1);
        Assert.True(Cv2.ImWrite(originalPath, original));

        using var resized = new Mat();
        Cv2.Resize(
            original,
            resized,
            new Size(96, 72),
            interpolation: InterpolationFlags.Area);
        Assert.True(Cv2.ImWrite(resizedPath, resized));
    }

    private static void CreateVideo(
        string path,
        int frameStart,
        int frameCount)
    {
        var size = new Size(128, 96);
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
            DrawVideoFrame(frame, frameStart + index);
            writer.Write(frame);
        }
    }

    private static void ReencodeVideoSegment(
        string sourcePath,
        string targetPath,
        long startFrame)
    {
        using var capture = new VideoCapture(sourcePath);
        Assert.True(capture.IsOpened());
        var size = new Size(
            (int)capture.Get(VideoCaptureProperties.FrameWidth),
            (int)capture.Get(VideoCaptureProperties.FrameHeight));
        using var writer = new VideoWriter(
            targetPath,
            VideoWriter.FourCC('M', 'J', 'P', 'G'),
            capture.Get(VideoCaptureProperties.Fps),
            size,
            isColor: true);
        Assert.True(writer.IsOpened());
        Assert.True(capture.Set(VideoCaptureProperties.PosFrames, startFrame));
        using var frame = new Mat();
        while (capture.Read(frame) && !frame.Empty())
        {
            writer.Write(frame);
        }
    }

    private static void DrawVideoFrame(Mat frame, int value)
    {
        frame.SetTo(new Scalar(value * 17 % 255, 60, 220 - value * 5 % 160));
        Cv2.Rectangle(
            frame,
            new Rect(8 + value * 5 % 70, 12 + value * 7 % 48, 34, 26),
            Scalar.White,
            thickness: -1);
        Cv2.Circle(
            frame,
            new Point(20 + value * 11 % 96, 72),
            10,
            new Scalar(20, 220, 40),
            thickness: -1);
        Cv2.PutText(
            frame,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new Point(4, 90),
            HersheyFonts.HersheySimplex,
            0.5,
            Scalar.Black,
            1);
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

    private sealed class NoVideoFingerprintService : IVideoFingerprintService
    {
        public Task<ProcessingResult<VideoFingerprint>> GetFingerprintAsync(
            FileEntry entry,
            bool forceRefresh,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The image acceptance fixture does not contain videos.");
    }
}
