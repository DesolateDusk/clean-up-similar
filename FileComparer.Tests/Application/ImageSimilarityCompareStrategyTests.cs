using FileComparer.Application.Compare;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class ImageSimilarityCompareStrategyTests
{
    [Fact]
    public async Task GroupsOnlySupportedImagesWhoseCandidatesVerify()
    {
        var files = new[]
        {
            Entry("a.jpg", size: 100),
            Entry("b.png", size: 100),
            Entry("far.bmp", size: 100),
            Entry("notes.txt", size: 100)
        };
        var fingerprints = new StubImageFingerprintService(
            new Dictionary<string, ProcessingResult<ImageFingerprint>>
            {
                [files[0].FullPath] = Success(files[0], 0),
                [files[1].FullPath] = Success(files[1], 0b11),
                [files[2].FullPath] = Success(files[2], ulong.MaxValue)
            });
        var verifier = new StubImageSimilarityVerifier((_, _) => Similar());
        var strategy = new ImageSimilarityCompareStrategy(
            fingerprints,
            new MultiBandImageCandidateIndex(),
            verifier);
        var progress = new List<ScanProgress>();

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                Request(forceRefresh: true),
                files,
                new InlineProgress<ScanProgress>(progress.Add)),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(CompareGroupType.SimilarImage, group.Type);
        Assert.Equal(
            new[] { files[0].FullPath, files[1].FullPath },
            group.Items.Select(item => item.File.FullPath).Order());
        Assert.Equal(3, fingerprints.Calls.Count);
        Assert.All(fingerprints.Calls, call => Assert.True(call.ForceRefresh));
        Assert.Single(verifier.Calls);
        Assert.DoesNotContain(
            fingerprints.Calls,
            call => call.Path == files[3].FullPath);
        Assert.Equal(ScanStage.Grouping, progress[^1].Stage);
        Assert.Equal(100, progress[^1].Percentage);
    }

    [Fact]
    public async Task BuildsConnectedGroupFromVerifiedMatches()
    {
        var files = new[]
        {
            Entry("keep.jpg", size: 300),
            Entry("middle.jpg", size: 200),
            Entry("edge.jpg", size: 100)
        };
        var fingerprints = new StubImageFingerprintService(
            new Dictionary<string, ProcessingResult<ImageFingerprint>>
            {
                [files[0].FullPath] = Success(files[0], 0, 300, 300),
                [files[1].FullPath] = Success(files[1], 1, 200, 200),
                [files[2].FullPath] = Success(files[2], 3, 100, 100)
            });
        var strategy = new ImageSimilarityCompareStrategy(
            fingerprints,
            new MultiBandImageCandidateIndex(),
            new StubImageSimilarityVerifier((_, _) => Similar()));

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(Request(), files),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Items.Count);
        var keep = Assert.Single(group.Items, item => item.IsSuggestedKeep);
        Assert.Equal(files[0].FullPath, keep.File.FullPath);
        Assert.Equal(0, keep.HammingDistance);
        Assert.Equal(1, keep.SimilarityScore);
    }

    [Fact]
    public async Task ChoosesKeepByResolutionSizeNewestTimeThenPath()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = old.AddDays(1);
        var files = new[]
        {
            Entry("small.jpg", 999, old),
            Entry("large-small-file.jpg", 100, old),
            Entry("large-big-old.jpg", 200, old),
            Entry("z-large-big-new.jpg", 200, recent),
            Entry("a-large-big-new.jpg", 200, recent)
        };
        var results = files.ToDictionary(
            file => file.FullPath,
            file => Success(
                file,
                hash: 0,
                width: file.Name == "small.jpg" ? 100 : 200,
                height: 100));
        var strategy = new ImageSimilarityCompareStrategy(
            new StubImageFingerprintService(results),
            new MultiBandImageCandidateIndex(),
            new StubImageSimilarityVerifier((_, _) => Similar()));

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(Request(), files),
            TestContext.Current.CancellationToken);

        var keep = Assert.Single(
            Assert.Single(groups).Items,
            item => item.IsSuggestedKeep);
        Assert.Equal(files[4].FullPath, keep.File.FullPath);
    }

    [Fact]
    public async Task ReportsFingerprintIssueAndContinuesOtherImages()
    {
        var files = new[]
        {
            Entry("a.jpg", size: 100),
            Entry("broken.jpg", size: 100),
            Entry("b.jpg", size: 100)
        };
        var issue = new ScanIssue(
            ScanStage.Fingerprinting,
            files[1].FullPath,
            ScanIssueCategory.InvalidImage,
            "圖片無法解碼。");
        var fingerprints = new StubImageFingerprintService(
            new Dictionary<string, ProcessingResult<ImageFingerprint>>
            {
                [files[0].FullPath] = Success(files[0], 0),
                [files[1].FullPath] =
                    ProcessingResult.Failure<ImageFingerprint>(issue),
                [files[2].FullPath] = Success(files[2], 0)
            });
        var reportedIssues = new List<ScanIssue>();
        var strategy = new ImageSimilarityCompareStrategy(
            fingerprints,
            new MultiBandImageCandidateIndex(),
            new StubImageSimilarityVerifier((_, _) => Similar()));

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                Request(),
                files,
                issueReporter: reportedIssues.Add),
            TestContext.Current.CancellationToken);

        Assert.Single(groups);
        Assert.Equal(issue, Assert.Single(reportedIssues));
    }

    private static ProcessingResult<ImageFingerprint> Success(
        FileEntry file,
        ulong hash,
        int width = 100,
        int height = 100) =>
        ProcessingResult.Success(
            new ImageFingerprint(
                file.FullPath,
                width,
                height,
                (double)width / height,
                hash));

    private static ProcessingResult<ImageSimilarityResult> Similar() =>
        ProcessingResult.Success(new ImageSimilarityResult(
            HashDistance: 0,
            MatchedFeatureCount: 24,
            InlierRatio: 0.8,
            FinalScore: 0.9,
            IsSimilar: true));

    private static FileEntry Entry(
        string name,
        long size,
        DateTime? lastWriteTimeUtc = null) =>
        new(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            size,
            lastWriteTimeUtc ??
                new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));

    private static ScanRequest Request(bool forceRefresh = false) =>
        new(
            "C:\\scan",
            IncludeSubdirectories: true,
            CompareMode.SimilarMedia,
            forceRefresh);
}
