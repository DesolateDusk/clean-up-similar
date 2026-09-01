using FileComparer.Application.Compare;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class VideoSimilarityCompareStrategyTests
{
    [Fact]
    public async Task GroupsTrimmedVideoByMatchingFramesAtDifferentPositions()
    {
        var original = Entry("original.avi");
        var trimmed = Entry("trimmed.avi");
        var unrelated = Entry("unrelated.avi");
        var service = new StubVideoFingerprintService(
            new Dictionary<string, ProcessingResult<VideoFingerprint>>
            {
                [original.FullPath] = Success(original, Frames(0, 12)),
                [trimmed.FullPath] = Success(
                    trimmed,
                    Frames(6, 6).Concat(Frames(60, 6)).ToArray()),
                [unrelated.FullPath] = Success(unrelated, Frames(40, 12))
            });
        var strategy = new VideoSimilarityCompareStrategy(service);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                Request(),
                [original, trimmed, unrelated]),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(CompareGroupType.SimilarVideo, group.Type);
        Assert.Equal(
            new[] { original.FullPath, trimmed.FullPath },
            group.Items.Select(item => item.File.FullPath).Order());
        Assert.All(service.Calls, call => Assert.False(call.ForceRefresh));
        Assert.Contains(group.Items, item => item.SimilarityScore is > 0 and < 1);
    }

    [Fact]
    public async Task DoesNotCompareIncompatibleAspectRatios()
    {
        var landscape = Entry("landscape.avi");
        var portrait = Entry("portrait.avi");
        var service = new StubVideoFingerprintService(
            new Dictionary<string, ProcessingResult<VideoFingerprint>>
            {
                [landscape.FullPath] = Success(
                    landscape,
                    Frames(0, 12),
                    width: 1920,
                    height: 1080),
                [portrait.FullPath] = Success(
                    portrait,
                    Frames(0, 12),
                    width: 1080,
                    height: 1920)
            });
        var strategy = new VideoSimilarityCompareStrategy(service);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(Request(), [landscape, portrait]),
            TestContext.Current.CancellationToken);

        Assert.Empty(groups);
    }

    private static ProcessingResult<VideoFingerprint> Success(
        FileEntry file,
        IReadOnlyList<VideoFrameFingerprint> frames,
        int width = 1280,
        int height = 720) =>
        ProcessingResult.Success(new VideoFingerprint(
            file.FullPath,
            TimeSpan.FromSeconds(12),
            width,
            height,
            "MJPG",
            frames));

    private static VideoFrameFingerprint[] Frames(int start, int count) =>
        Enumerable.Range(start, count)
            .Select((value, index) => new VideoFrameFingerprint(
                index / 11d,
                TimeSpan.FromSeconds(index),
                HashFor(value),
                Contrast: 30))
            .ToArray();

    private static ulong HashFor(int value)
    {
        var hash = (ulong)value + 0x9E3779B97F4A7C15UL;
        hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
        hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
        return hash ^ (hash >> 31);
    }

    private static FileEntry Entry(string name) =>
        new(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            100,
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));

    private static ScanRequest Request() =>
        new("C:\\scan", IncludeSubdirectories: true, CompareMode.SimilarMedia);
}
