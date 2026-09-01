using FileComparer.Domain.Models;
using Xunit;

namespace FileComparer.Tests.Domain;

public sealed class MediaComparisonModelsTests
{
    [Fact]
    public void SimilarMediaRequestAcceptsTheCombinedMediaMode()
    {
        var request = new ScanRequest(
            "C:\\scan",
            IncludeSubdirectories: true,
            CompareMode.SimilarMedia);

        Assert.Empty(request.Validate());
    }

    [Fact]
    public void CompareItemCanCarryVideoFingerprintAndSimilarityScore()
    {
        var file = new FileEntry(
            "C:\\scan\\clip.avi",
            "clip.avi",
            ".avi",
            1024,
            new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));
        var video = new VideoFingerprint(
            file.FullPath,
            TimeSpan.FromSeconds(12),
            1920,
            1080,
            "MJPG",
            [
                new VideoFrameFingerprint(
                    PositionRatio: 0,
                    Timestamp: TimeSpan.Zero,
                    PerceptualHash: 42,
                    Contrast: 18)
            ]);

        var item = new CompareItem(
            file,
            Video: video,
            SimilarityScore: 0.92);

        Assert.Same(video, item.Video);
        Assert.Equal(0.92, item.SimilarityScore);
    }
}
