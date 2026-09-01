using FileComparer.Domain.Models;
using FileComparer.Infrastructure.FileSystem;
using FileComparer.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class DirectoryFileScannerTests
{
    private static readonly string[] ShallowNames = ["root.txt"];
    private static readonly string[] RecursiveNames =
        ["nested.txt", "root.txt"];

    [Fact]
    public async Task HonorsRecursiveOptionAndReportsDiscoveredFiles()
    {
        using var temp = new TemporaryDirectory();
        var child = Directory.CreateDirectory(temp.GetPath("child")).FullName;
        await File.WriteAllTextAsync(
            temp.GetPath("root.txt"),
            "root",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(child, "nested.txt"),
            "nested",
            TestContext.Current.CancellationToken);
        var scanner = CreateScanner();
        var progress = new List<ScanProgress>();

        var shallow = await scanner.ScanAsync(
            Request(temp, recursive: false),
            new InlineProgress<ScanProgress>(progress.Add),
            TestContext.Current.CancellationToken);
        var recursive = await scanner.ScanAsync(
            Request(temp, recursive: true),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ShallowNames,
            shallow.Files.Select(file => file.Name));
        Assert.Equal(
            RecursiveNames,
            recursive.Files.Select(file => file.Name).Order());
        Assert.Empty(shallow.Issues);
        Assert.Empty(recursive.Issues);
        Assert.Contains(
            progress,
            update => update.Stage == ScanStage.Enumerating &&
                      update.DiscoveredCount == 1);
    }

    [Fact]
    public async Task MissingRootReturnsIssueWithoutThrowing()
    {
        using var temp = new TemporaryDirectory();
        var missing = temp.GetPath("missing");
        var scanner = CreateScanner();

        var result = await scanner.ScanAsync(
            new ScanRequest(
                missing,
                true,
                CompareMode.ExactFiles),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Files);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ScanIssueCategory.NotFound, issue.Category);
        Assert.Equal(missing, issue.Path);
    }

    [Fact]
    public async Task ObservesCancellationBeforeEnumeration()
    {
        using var temp = new TemporaryDirectory();
        var scanner = CreateScanner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => scanner.ScanAsync(
                Request(temp, recursive: true),
                progress: null,
                cancellation.Token));
    }

    private static DirectoryFileScanner CreateScanner() =>
        new(NullLogger<DirectoryFileScanner>.Instance);

    private static ScanRequest Request(
        TemporaryDirectory temp,
        bool recursive) =>
        new(
            temp.FullPath,
            recursive,
            CompareMode.ExactFiles);
}
