using FileComparer.Application.Delete;
using FileComparer.Domain.Models;
using FileComparer.Tests.TestDoubles;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class DeleteApplicationServiceTests
{
    [Fact]
    public void SummarizesDistinctSelectedFilesThatHaveNotBeenRecycled()
    {
        var active = Item("active.bin", 100);
        var failed = Item("failed.bin", 200) with
        {
            Status = CompareItemStatus.DeleteFailed
        };
        var recycled = Item("recycled.bin", 400) with
        {
            Status = CompareItemStatus.Recycled
        };
        var groups = new[] { Group(active, failed, recycled) };
        var service = CreateService();

        var summary = service.Summarize(
            groups,
            [
                active.File.FullPath,
                active.File.FullPath.ToUpperInvariant(),
                failed.File.FullPath,
                recycled.File.FullPath,
                "C:\\scan\\unknown.bin"
            ]);

        Assert.Equal(2, summary.Count);
        Assert.Equal(300, summary.TotalBytes);
    }

    [Fact]
    public async Task DoesNothingUntilDeletionIsConfirmed()
    {
        var item = Item("selected.bin", 100);
        var group = Group(item);
        var recycleBin = new StubRecycleBinService((path, _) =>
            Task.FromResult(new DeleteOutcome(path, IsSuccess: true)));
        var service = new DeleteApplicationService(recycleBin);

        var result = await service.DeleteAsync(
            [group],
            [item.File.FullPath],
            confirmed: false,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(recycleBin.Paths);
        Assert.Empty(result.Outcomes);
        Assert.Equal(group, Assert.Single(result.UpdatedGroups));
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public async Task UpdatesEachItemAfterPartialRecycleBinFailure()
    {
        var success = Item("success.bin", 100);
        var failure = Item("failure.bin", 200);
        var untouched = Item("untouched.bin", 300);
        var group = Group(success, failure, untouched);
        var recycleBin = new StubRecycleBinService((path, _) =>
            Task.FromResult(path == success.File.FullPath
                ? new DeleteOutcome(path, IsSuccess: true)
                : new DeleteOutcome(path, IsSuccess: false, "檔案被占用。")));
        var service = new DeleteApplicationService(recycleBin);
        var progress = new List<ScanProgress>();

        var result = await service.DeleteAsync(
            [group],
            [success.File.FullPath, failure.File.FullPath],
            confirmed: true,
            new InlineProgress<ScanProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Outcomes.Count);
        var updated = Assert.Single(result.UpdatedGroups);
        Assert.Equal(
            CompareItemStatus.Recycled,
            Find(updated, success.File.FullPath).Status);
        var failed = Find(updated, failure.File.FullPath);
        Assert.Equal(CompareItemStatus.DeleteFailed, failed.Status);
        Assert.Equal("檔案被占用。", failed.StatusMessage);
        Assert.Equal(
            CompareItemStatus.Active,
            Find(updated, untouched.File.FullPath).Status);
        Assert.Equal(100, progress[^1].Percentage);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public async Task ReturnsCompletedWorkWhenCancellationStopsBatch()
    {
        var first = Item("first.bin", 100);
        var second = Item("second.bin", 200);
        var group = Group(first, second);
        using var cancellation = new CancellationTokenSource();
        var recycleBin = new StubRecycleBinService((path, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new DeleteOutcome(path, IsSuccess: true));
        });
        var service = new DeleteApplicationService(recycleBin);

        var result = await service.DeleteAsync(
            [group],
            [first.File.FullPath, second.File.FullPath],
            confirmed: true,
            progress: null,
            cancellation.Token);

        Assert.True(result.IsCancelled);
        Assert.Single(result.Outcomes);
        Assert.Single(recycleBin.Paths);
        var updated = Assert.Single(result.UpdatedGroups);
        Assert.Equal(
            CompareItemStatus.Recycled,
            Find(updated, first.File.FullPath).Status);
        Assert.Equal(
            CompareItemStatus.Active,
            Find(updated, second.File.FullPath).Status);
    }

    private static DeleteApplicationService CreateService() =>
        new(new StubRecycleBinService((path, _) =>
            Task.FromResult(new DeleteOutcome(path, IsSuccess: true))));

    private static CompareItem Find(CompareGroup group, string path) =>
        Assert.Single(
            group.Items,
            item => string.Equals(
                item.File.FullPath,
                path,
                StringComparison.OrdinalIgnoreCase));

    private static CompareGroup Group(params CompareItem[] items) =>
        new(Guid.NewGuid(), CompareGroupType.ExactDuplicate, items);

    private static CompareItem Item(string name, long size) =>
        new(new FileEntry(
            Path.Combine("C:\\scan", name),
            name,
            Path.GetExtension(name),
            size,
            new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc)));
}
