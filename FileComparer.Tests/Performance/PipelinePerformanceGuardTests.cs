using FileComparer.Application.Compare;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Performance;

public sealed class PipelinePerformanceGuardTests
{
    [Fact]
    public void MultiBandIndexPrunesMostRandomPairComparisons()
    {
        const int count = 2048;
        var index = new MultiBandImageCandidateIndex();
        index.Reset(maxHammingDistance: 10);
        var state = 0x9E3779B97F4A7C15UL;

        for (var itemIndex = 0; itemIndex < count; itemIndex++)
        {
            state = Next(state);
            var fingerprint = new ImageFingerprint(
                $"C:\\images\\{itemIndex:D5}.jpg",
                100,
                100,
                1,
                state);
            _ = index.FindCandidates(fingerprint).Count();
            index.Add(fingerprint);
        }

        var allPairs = (long)count * (count - 1) / 2;
        Assert.InRange(index.CandidateCount, 0, allPairs / 3);
    }

    [Fact]
    public async Task UniqueSizesDoNotTriggerAnyHashIoAtScale()
    {
        var files = Enumerable.Range(1, 10_000)
            .Select(index => new FileEntry(
                $"C:\\scan\\{index:D5}.bin",
                $"{index:D5}.bin",
                ".bin",
                index,
                DateTime.UnixEpoch))
            .ToArray();
        var quick = new StubQuickHashService(
            new Dictionary<string, ProcessingResult<string>>());
        var full = new StubHashService(
            new Dictionary<string, ProcessingResult<string>>());
        var strategy = new ExactFileCompareStrategy(quick, full);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                new ScanRequest(
                    "C:\\scan",
                    IncludeSubdirectories: true,
                    CompareMode.ExactFiles),
                files),
            TestContext.Current.CancellationToken);

        Assert.Empty(groups);
        Assert.Equal(0, quick.TotalCalls);
        Assert.Equal(0, full.TotalCalls);
    }

    private static ulong Next(ulong value)
    {
        value ^= value << 13;
        value ^= value >> 7;
        value ^= value << 17;
        return value;
    }
}
