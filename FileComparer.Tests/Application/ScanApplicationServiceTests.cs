using FileComparer.Application.Scan;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class ScanApplicationServiceTests
{
    [Fact]
    public async Task InvalidRequestDoesNotStartScanner()
    {
        var scanner = new StubFileScanner(
            (_, _, _) => throw new InvalidOperationException());
        var strategy = Strategy(CompareMode.ExactFiles);
        var service = CreateService(scanner, strategy);

        var result = await service.ScanAsync(
            new ScanRequest(
                string.Empty,
                true,
                CompareMode.ExactFiles),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanStatus.Failed, result.Status);
        Assert.Equal(0, scanner.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ScanIssueCategory.InvalidInput, issue.Category);
    }

    [Fact]
    public async Task CompletedScanCombinesScannerAndStrategyIssues()
    {
        var file = Entry("a.bin");
        var scannerIssue = Issue("scanner");
        var strategyIssue = Issue("strategy");
        var scanner = new StubFileScanner(
            (_, _, _) => Task.FromResult(
                new FileScanResult(
                    new[] { file },
                    new[] { scannerIssue })));
        var expectedGroup = new CompareGroup(
            Guid.NewGuid(),
            CompareGroupType.ExactDuplicate,
            new[] { new CompareItem(file), new CompareItem(Entry("b.bin")) });
        var strategy = new StubCompareStrategy(
            CompareMode.ExactFiles,
            (context, _) =>
            {
                context.ReportIssue(strategyIssue);
                return Task.FromResult<IReadOnlyList<CompareGroup>>(
                    new[] { expectedGroup });
            });
        var progress = new List<ScanProgress>();
        var service = CreateService(scanner, strategy);

        var result = await service.ScanAsync(
            Request(),
            new InlineProgress<ScanProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(expectedGroup, Assert.Single(result.Groups));
        Assert.Equal(
            new[] { scannerIssue, strategyIssue },
            result.Issues);
        Assert.Equal(1, strategy.Calls);
        Assert.Contains(
            progress,
            update => update.Stage == ScanStage.Completed &&
                      update.Percentage == 100);
    }

    [Fact]
    public async Task UsesOnlyStrategyMatchingRequestedMode()
    {
        var scanner = new StubFileScanner(
            (_, _, _) => Task.FromResult(
                new FileScanResult(
                    Array.Empty<FileEntry>(),
                    Array.Empty<ScanIssue>())));
        var exact = Strategy(CompareMode.ExactFiles);
        var media = Strategy(CompareMode.SimilarMedia);
        var service = CreateService(scanner, exact, media);

        await service.ScanAsync(
            Request() with { Mode = CompareMode.SimilarMedia },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exact.Calls);
        Assert.Equal(1, media.Calls);
    }

    [Fact]
    public async Task CancellationReturnsCancelledResult()
    {
        var scanner = new StubFileScanner(
            (_, _, token) =>
                Task.FromCanceled<FileScanResult>(
                    token.IsCancellationRequested
                        ? token
                        : new CancellationToken(canceled: true)));
        var service = CreateService(
            scanner,
            Strategy(CompareMode.ExactFiles));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.ScanAsync(
            Request(),
            progress: null,
            cancellation.Token);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Empty(result.Groups);
        Assert.Null(result.ErrorMessage);
    }

    private static ScanApplicationService CreateService(
        IFileScanner scanner,
        params ICompareStrategy[] strategies) =>
        new(
            scanner,
            strategies,
            NullLogger<ScanApplicationService>.Instance);

    private static StubCompareStrategy Strategy(CompareMode mode) =>
        new(
            mode,
            (_, _) => Task.FromResult<IReadOnlyList<CompareGroup>>(
                Array.Empty<CompareGroup>()));

    private static ScanRequest Request() =>
        new("C:\\scan", true, CompareMode.ExactFiles);

    private static FileEntry Entry(string name) =>
        new(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            10,
            DateTime.UnixEpoch);

    private static ScanIssue Issue(string message) =>
        new(
            ScanStage.Enumerating,
            null,
            ScanIssueCategory.Unknown,
            message);
}
