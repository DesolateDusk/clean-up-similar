using FileComparer.Desktop.ViewModels;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Xunit;

namespace FileComparer.Tests.Desktop;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task CompletedScanMapsGroupsAndIssuesToResultState()
    {
        var group = Group(Item("a.bin"), Item("b.bin"));
        var issue = new ScanIssue(
            ScanStage.Fingerprinting,
            "C:\\scan\\locked.bin",
            ScanIssueCategory.Locked,
            "檔案被占用。");
        var scan = new StubScanApplicationService((_, progress, _) =>
        {
            progress?.Report(new ScanProgress(
                ScanStage.Fingerprinting,
                3,
                2,
                66));
            return Task.FromResult(new ScanResult(
                ScanStatus.Completed,
                [group],
                [issue],
                3,
                3));
        });
        using var viewModel = CreateViewModel(scan);
        viewModel.RootDirectory = "C:\\scan";

        await viewModel.StartAsync();

        Assert.True(viewModel.IsResultsVisible);
        Assert.False(viewModel.IsOperationActive);
        Assert.Equal(3, viewModel.DiscoveredCount);
        Assert.Equal(100, viewModel.ProgressPercentage);
        Assert.Single(viewModel.Groups);
        Assert.Equal(issue, Assert.Single(viewModel.Issues));
        Assert.Contains("1", viewModel.IssueSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewIsLoadedOnlyAfterItemIsSelected()
    {
        var item = Item("photo.png", image: true);
        var thumbnails = new StubThumbnailService();
        using var viewModel = CreateViewModel(
            new StubScanApplicationService((_, _, _) =>
                Task.FromResult(new ScanResult(
                    ScanStatus.Completed,
                    [Group(item, Item("copy.png", image: true))],
                    [],
                    2,
                    2))),
            thumbnails);
        viewModel.RootDirectory = "C:\\scan";
        await viewModel.StartAsync();

        Assert.Equal(0, thumbnails.Calls);
        var selected = viewModel.Groups[0].Items[0];
        await viewModel.SelectItemAsync(selected);

        Assert.Equal(1, thumbnails.Calls);
        Assert.Equal([1, 2, 3], viewModel.PreviewPng);
        Assert.Equal(selected, viewModel.SelectedItem);
    }

    [Fact]
    public async Task MediaModeSubmitsTheSingleCombinedMediaMode()
    {
        ScanRequest? submittedRequest = null;
        using var viewModel = CreateViewModel(
            new StubScanApplicationService((request, _, _) =>
            {
                submittedRequest = request;
                return Task.FromResult(new ScanResult(
                    ScanStatus.Completed,
                    [],
                    [],
                    0,
                    0));
            }));
        viewModel.RootDirectory = "C:\\scan";
        viewModel.IsMediaMode = true;

        await viewModel.StartAsync();

        Assert.Equal(CompareMode.SimilarMedia, submittedRequest?.Mode);
        Assert.True(viewModel.IsMediaMode);
        Assert.False(viewModel.IsExactMode);
    }

    [Fact]
    public async Task ConfirmedDeleteRefreshesItemStatusAndSelectionSummary()
    {
        var first = Item("first.bin");
        var second = Item("second.bin");
        var group = Group(first, second);
        var delete = new StubDeleteApplicationService();
        using var viewModel = CreateViewModel(
            new StubScanApplicationService((_, _, _) =>
                Task.FromResult(new ScanResult(
                    ScanStatus.Completed,
                    [group],
                    [],
                    2,
                    2))),
            deleteService: delete);
        viewModel.RootDirectory = "C:\\scan";
        await viewModel.StartAsync();
        viewModel.Groups[0].Items[0].IsSelectedForDelete = true;

        var summary = viewModel.GetDeleteSummary();
        await viewModel.DeleteSelectedAsync(confirmed: true);

        Assert.Equal(1, summary.Count);
        Assert.Equal(1, delete.DeleteCalls);
        Assert.Equal(
            CompareItemStatus.Recycled,
            viewModel.Groups[0].Items[0].Status);
        Assert.False(viewModel.Groups[0].Items[0].IsSelectedForDelete);
    }

    [Fact]
    public async Task SuggestedSelectionAlwaysLeavesOneAvailableItemPerGroup()
    {
        using var viewModel = CreateViewModel(
            new StubScanApplicationService((_, _, _) =>
                Task.FromResult(new ScanResult(
                    ScanStatus.Completed,
                    [Group(Item("a.bin"), Item("b.bin"), Item("c.bin"))],
                    [],
                    3,
                    3))));
        viewModel.RootDirectory = "C:\\scan";
        await viewModel.StartAsync();

        viewModel.SelectSuggestedDeletes();

        var items = viewModel.Groups[0].Items;
        Assert.Equal(2, items.Count(item => item.IsSelectedForDelete));
        Assert.Single(items, item => !item.IsSelectedForDelete);
    }

    [Fact]
    public async Task CancelStopsActiveScanAndReturnsToStartState()
    {
        var scan = new StubScanApplicationService(async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                return new ScanResult(
                    ScanStatus.Cancelled,
                    [],
                    [],
                    0,
                    0);
            }

            throw new InvalidOperationException("Unreachable");
        });
        using var viewModel = CreateViewModel(scan);
        viewModel.RootDirectory = "C:\\scan";

        var operation = viewModel.StartAsync();
        viewModel.CancelCurrentOperation();
        await operation;

        Assert.True(viewModel.IsStartVisible);
        Assert.False(viewModel.IsOperationActive);
        Assert.Contains("取消", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IScanApplicationService scanService,
        IThumbnailService? thumbnailService = null,
        IDeleteApplicationService? deleteService = null) =>
        new(
            scanService,
            deleteService ?? new StubDeleteApplicationService(),
            thumbnailService ?? new StubThumbnailService(),
            new StubFileLauncher());

    private static CompareGroup Group(params CompareItem[] items) =>
        new(Guid.NewGuid(), CompareGroupType.ExactDuplicate, items);

    private static CompareItem Item(string name, bool image = false)
    {
        var file = new FileEntry(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            100,
            new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));
        return new CompareItem(
            file,
            image
                ? new ImageFingerprint(file.FullPath, 100, 80, 1.25, 0)
                : null);
    }

    private sealed class StubScanApplicationService(
        Func<ScanRequest, IProgress<ScanProgress>?, CancellationToken, Task<ScanResult>>
            handler)
        : IScanApplicationService
    {
        public Task<ScanResult> ScanAsync(
            ScanRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) =>
            handler(request, progress, cancellationToken);
    }

    private sealed class StubThumbnailService : IThumbnailService
    {
        public int Calls { get; private set; }

        public Task<ProcessingResult<byte[]>> CreateThumbnailAsync(
            string path,
            int maxWidth,
            int maxHeight,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ProcessingResult.Success<byte[]>([1, 2, 3]));
        }
    }

    private sealed class StubFileLauncher : IFileLauncher
    {
        public Task<FileActionResult> OpenFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FileActionResult(IsSuccess: true));

        public Task<FileActionResult> OpenContainingFolderAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FileActionResult(IsSuccess: true));
    }

    private sealed class StubDeleteApplicationService : IDeleteApplicationService
    {
        public int DeleteCalls { get; private set; }

        public DeleteSummary Summarize(
            IReadOnlyCollection<CompareGroup> groups,
            IReadOnlyCollection<string> selectedPaths)
        {
            var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = groups
                .SelectMany(group => group.Items)
                .Where(item => selected.Contains(item.File.FullPath))
                .ToArray();
            return new DeleteSummary(items.Length, items.Sum(item => item.File.Size));
        }

        public Task<DeleteBatchResult> DeleteAsync(
            IReadOnlyCollection<CompareGroup> groups,
            IReadOnlyCollection<string> selectedPaths,
            bool confirmed,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updated = groups
                .Select(group => group with
                {
                    Items = group.Items
                        .Select(item => selected.Contains(item.File.FullPath)
                            ? item with { Status = CompareItemStatus.Recycled }
                            : item)
                        .ToArray()
                })
                .ToArray();
            return Task.FromResult(new DeleteBatchResult(
                Summarize(groups, selectedPaths),
                [],
                updated));
        }
    }
}
