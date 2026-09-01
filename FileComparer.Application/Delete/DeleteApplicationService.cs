using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Application.Delete;

public sealed class DeleteApplicationService(IRecycleBinService recycleBinService)
    : IDeleteApplicationService
{
    public DeleteSummary Summarize(
        IReadOnlyCollection<CompareGroup> groups,
        IReadOnlyCollection<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        var eligible = GetEligibleItems(groups, selectedPaths);
        return new DeleteSummary(
            eligible.Length,
            eligible.Sum(item => item.File.Size));
    }

    public async Task<DeleteBatchResult> DeleteAsync(
        IReadOnlyCollection<CompareGroup> groups,
        IReadOnlyCollection<string> selectedPaths,
        bool confirmed,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        var originalGroups = groups.ToArray();
        var eligible = GetEligibleItems(groups, selectedPaths);
        var summary = new DeleteSummary(
            eligible.Length,
            eligible.Sum(item => item.File.Size));
        if (!confirmed || eligible.Length == 0)
        {
            return new DeleteBatchResult(
                summary,
                Array.Empty<DeleteOutcome>(),
                originalGroups);
        }

        var updatedGroups = originalGroups;
        var outcomes = new List<DeleteOutcome>(eligible.Length);
        var isCancelled = false;

        foreach (var item in eligible)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                isCancelled = true;
                break;
            }

            DeleteOutcome outcome;
            try
            {
                outcome = await recycleBinService
                    .RecycleAsync(item.File.FullPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                isCancelled = true;
                break;
            }

            outcomes.Add(outcome);
            updatedGroups = ApplyOutcome(updatedGroups, outcome);
            progress?.Report(new ScanProgress(
                ScanStage.Deleting,
                eligible.Length,
                outcomes.Count,
                outcomes.Count * 100d / eligible.Length,
                item.File.FullPath));
        }

        if (cancellationToken.IsCancellationRequested &&
            outcomes.Count < eligible.Length)
        {
            isCancelled = true;
        }

        return new DeleteBatchResult(
            summary,
            outcomes,
            updatedGroups,
            isCancelled);
    }

    private static CompareItem[] GetEligibleItems(
        IEnumerable<CompareGroup> groups,
        IEnumerable<string> selectedPaths)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups
            .SelectMany(group => group.Items)
            .Where(item =>
                item.Status != CompareItemStatus.Recycled &&
                selected.Contains(item.File.FullPath))
            .GroupBy(
                item => item.File.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(
                item => item.File.FullPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompareGroup[] ApplyOutcome(
        IEnumerable<CompareGroup> groups,
        DeleteOutcome outcome) =>
        groups
            .Select(group => group with
            {
                Items = group.Items
                    .Select(item => string.Equals(
                        item.File.FullPath,
                        outcome.Path,
                        StringComparison.OrdinalIgnoreCase)
                        ? item with
                        {
                            Status = outcome.IsSuccess
                                ? CompareItemStatus.Recycled
                                : CompareItemStatus.DeleteFailed,
                            StatusMessage = outcome.IsSuccess
                                ? null
                                : outcome.ErrorMessage
                        }
                        : item)
                    .ToArray()
            })
            .ToArray();
}
