using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Extensions.Logging;

namespace FileComparer.Application.Scan;

/// <summary>
/// Coordinates one scan operation at a time and converts cancellation or
/// unexpected failures into explicit result states for the desktop UI.
/// </summary>
public sealed class ScanApplicationService(
    IFileScanner fileScanner,
    IEnumerable<ICompareStrategy> strategies,
    ILogger<ScanApplicationService> logger)
    : IScanApplicationService
{
    private static readonly Action<ILogger, Exception?>
        LogUnexpectedFailure = LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2001, nameof(LogUnexpectedFailure)),
            "Unexpected scan operation failure");

    private readonly Dictionary<CompareMode, ICompareStrategy>
        strategiesByMode = strategies.ToDictionary(strategy => strategy.Mode);
    private int isRunning;

    public async Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationIssues = request.Validate()
            .Select(message => new ScanIssue(
                ScanStage.Idle,
                request.RootDirectory,
                ScanIssueCategory.InvalidInput,
                message))
            .ToArray();
        if (validationIssues.Length > 0)
        {
            return Failed(
                validationIssues,
                "掃描設定有誤。",
                discoveredCount: 0);
        }

        if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            return Failed(
                new[]
                {
                    new ScanIssue(
                        ScanStage.Idle,
                        request.RootDirectory,
                        ScanIssueCategory.InvalidInput,
                        "已有掃描正在執行。")
                },
                "已有掃描正在執行。",
                discoveredCount: 0);
        }

        var issues = new List<ScanIssue>();
        var discovered = 0;
        var processed = 0;

        try
        {
            progress?.Report(new ScanProgress(
                ScanStage.Enumerating,
                0,
                0));
            var fileScan = await fileScanner
                .ScanAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
            discovered = fileScan.Files.Count;
            issues.AddRange(fileScan.Issues);

            if (!strategiesByMode.TryGetValue(
                    request.Mode,
                    out var strategy))
            {
                issues.Add(new ScanIssue(
                    ScanStage.Failed,
                    request.RootDirectory,
                    ScanIssueCategory.InvalidInput,
                    "找不到指定的比對模式。"));
                return Failed(
                    issues,
                    "找不到指定的比對模式。",
                    discovered);
            }

            var context = new CompareOperationContext(
                request,
                fileScan.Files,
                progress,
                issues.Add);
            var groups = await strategy
                .CompareAsync(context, cancellationToken)
                .ConfigureAwait(false);
            processed = discovered;
            progress?.Report(new ScanProgress(
                ScanStage.Completed,
                discovered,
                processed,
                Percentage: 100));

            return new ScanResult(
                ScanStatus.Completed,
                groups,
                issues,
                discovered,
                processed);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new ScanProgress(
                ScanStage.Cancelled,
                discovered,
                processed));
            return new ScanResult(
                ScanStatus.Cancelled,
                Array.Empty<CompareGroup>(),
                issues,
                discovered,
                processed);
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(logger, exception);
            issues.Add(new ScanIssue(
                ScanStage.Failed,
                request.RootDirectory,
                ScanIssueCategory.Unknown,
                "掃描發生未預期錯誤，請查看本機日誌。"));
            progress?.Report(new ScanProgress(
                ScanStage.Failed,
                discovered,
                processed));
            return Failed(
                issues,
                "掃描失敗，請查看本機日誌。",
                discovered,
                processed);
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
        }
    }

    private static ScanResult Failed(
        IReadOnlyList<ScanIssue> issues,
        string message,
        int discoveredCount,
        int processedCount = 0) =>
        new(
            ScanStatus.Failed,
            Array.Empty<CompareGroup>(),
            issues,
            discoveredCount,
            processedCount,
            message);
}
