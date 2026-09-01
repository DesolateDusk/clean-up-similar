using FileComparer.Domain.Models;

namespace FileComparer.Domain.Interfaces;

public interface ICompareStrategy
{
    CompareMode Mode { get; }

    Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken);
}

public interface IScanApplicationService
{
    Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IDeleteApplicationService
{
    DeleteSummary Summarize(
        IReadOnlyCollection<CompareGroup> groups,
        IReadOnlyCollection<string> selectedPaths);

    Task<DeleteBatchResult> DeleteAsync(
        IReadOnlyCollection<CompareGroup> groups,
        IReadOnlyCollection<string> selectedPaths,
        bool confirmed,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
