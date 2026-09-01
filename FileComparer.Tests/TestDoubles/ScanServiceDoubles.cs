using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubFileScanner(
    Func<ScanRequest, IProgress<ScanProgress>?, CancellationToken, Task<FileScanResult>>
        handler)
    : IFileScanner
{
    public int Calls { get; private set; }

    public Task<FileScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Calls++;
        return handler(request, progress, cancellationToken);
    }
}

internal sealed class StubCompareStrategy(
    CompareMode mode,
    Func<CompareOperationContext, CancellationToken, Task<IReadOnlyList<CompareGroup>>>
        handler)
    : ICompareStrategy
{
    public int Calls { get; private set; }

    public CompareMode Mode { get; } = mode;

    public Task<IReadOnlyList<CompareGroup>> CompareAsync(
        CompareOperationContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        return handler(context, cancellationToken);
    }
}
