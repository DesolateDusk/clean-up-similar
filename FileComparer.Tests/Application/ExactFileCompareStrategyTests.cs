using FileComparer.Application.Compare;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class ExactFileCompareStrategyTests
{
    [Fact]
    public async Task GroupsOnlyFilesWhoseFullHashesMatch()
    {
        var files = new[]
        {
            Entry("a.bin", 10),
            Entry("b.txt", 10),
            Entry("c.bin", 10),
            Entry("unique.bin", 99)
        };
        var quick = new StubQuickHashService(new Dictionary<string, ProcessingResult<string>>
        {
            [files[0].FullPath] = Success("quick-same"),
            [files[1].FullPath] = Success("quick-same"),
            [files[2].FullPath] = Success("quick-same")
        });
        var full = new StubHashService(new Dictionary<string, ProcessingResult<string>>
        {
            [files[0].FullPath] = Success("sha-same"),
            [files[1].FullPath] = Success("sha-same"),
            [files[2].FullPath] = Success("sha-different")
        });
        var issues = new List<ScanIssue>();
        var strategy = new ExactFileCompareStrategy(quick, full);
        var context = new CompareOperationContext(
            Request(),
            files,
            issueReporter: issues.Add);

        var groups = await strategy.CompareAsync(context, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(CompareGroupType.ExactDuplicate, group.Type);
        Assert.Equal(
            new[] { files[0].FullPath, files[1].FullPath },
            group.Items.Select(item => item.File.FullPath).Order());
        Assert.Equal(3, quick.TotalCalls);
        Assert.Equal(3, full.TotalCalls);
        Assert.Equal(0, quick.CallsFor(files[3].FullPath));
        Assert.Single(group.Items, item => item.IsSuggestedKeep);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task DoesNotRunFullHashWhenQuickHashesAreUnique()
    {
        var files = new[] { Entry("a.bin", 10), Entry("b.bin", 10) };
        var quick = new StubQuickHashService(new Dictionary<string, ProcessingResult<string>>
        {
            [files[0].FullPath] = Success("quick-a"),
            [files[1].FullPath] = Success("quick-b")
        });
        var full = new StubHashService(
            new Dictionary<string, ProcessingResult<string>>());
        var strategy = new ExactFileCompareStrategy(quick, full);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(Request(), files),
            CancellationToken.None);

        Assert.Empty(groups);
        Assert.Equal(2, quick.TotalCalls);
        Assert.Equal(0, full.TotalCalls);
    }

    [Fact]
    public async Task ReportsSingleFileHashFailureAndContinues()
    {
        var files = new[]
        {
            Entry("a.bin", 10),
            Entry("b.bin", 10),
            Entry("c.bin", 10)
        };
        var issue = new ScanIssue(
            ScanStage.Fingerprinting,
            files[2].FullPath,
            ScanIssueCategory.Locked,
            "檔案使用中。");
        var quick = new StubQuickHashService(new Dictionary<string, ProcessingResult<string>>
        {
            [files[0].FullPath] = Success("quick"),
            [files[1].FullPath] = Success("quick"),
            [files[2].FullPath] = ProcessingResult.Failure<string>(issue)
        });
        var full = new StubHashService(new Dictionary<string, ProcessingResult<string>>
        {
            [files[0].FullPath] = Success("sha"),
            [files[1].FullPath] = Success("sha")
        });
        var issues = new List<ScanIssue>();
        var strategy = new ExactFileCompareStrategy(quick, full);

        var groups = await strategy.CompareAsync(
            new CompareOperationContext(
                Request(),
                files,
                issueReporter: issues.Add),
            CancellationToken.None);

        Assert.Single(groups);
        Assert.Equal(issue, Assert.Single(issues));
    }

    private static ProcessingResult<string> Success(string value) =>
        ProcessingResult.Success(value);

    private static FileEntry Entry(string name, long size) =>
        new(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            size,
            new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));

    private static ScanRequest Request() =>
        new("C:\\scan", true, CompareMode.ExactFiles);
}
