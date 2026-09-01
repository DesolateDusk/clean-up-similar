using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Extensions.Logging;

namespace FileComparer.Infrastructure.FileSystem;

/// <summary>
/// Enumerates file metadata without loading file contents. Failures are scoped
/// to the current path so one inaccessible item cannot abort the full scan.
/// </summary>
public sealed class DirectoryFileScanner(
    ILogger<DirectoryFileScanner> logger)
    : IFileScanner
{
    private static readonly Action<ILogger, string, string, Exception?>
        LogSkippedPath = LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1101, nameof(LogSkippedPath)),
            "Skipped {Path} while enumerating files: {Category}");

    public Task<FileScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () => ScanCore(request, progress, cancellationToken),
            cancellationToken);
    }

    private FileScanResult ScanCore(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<FileEntry>();
        var issues = new List<ScanIssue>();
        string root;

        try
        {
            root = Path.GetFullPath(request.RootDirectory);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                  PathTooLongException)
        {
            issues.Add(CreateIssue(
                request.RootDirectory,
                exception,
                ScanIssueCategory.InvalidInput));
            return new FileScanResult(files, issues);
        }

        if (!Directory.Exists(root))
        {
            issues.Add(new ScanIssue(
                ScanStage.Enumerating,
                root,
                ScanIssueCategory.NotFound,
                "掃描目錄不存在。"));
            return new FileScanResult(files, issues);
        }

        try
        {
            if (!DirectoryTraversalRules.CanProcess(File.GetAttributes(root)))
            {
                AddReparsePointIssue(issues, root);
                return new FileScanResult(files, issues);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemError(exception))
        {
            AddIssue(issues, root, exception, Classify(exception));
            return new FileScanResult(files, issues);
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            EnumerateFiles(
                currentDirectory,
                files,
                issues,
                progress,
                cancellationToken);

            if (!request.IncludeSubdirectories)
            {
                continue;
            }

            try
            {
                foreach (var childDirectory in Directory.EnumerateDirectories(
                             currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!DirectoryTraversalRules.CanProcess(
                                File.GetAttributes(childDirectory)))
                        {
                            AddReparsePointIssue(issues, childDirectory);
                            continue;
                        }

                        pendingDirectories.Push(childDirectory);
                    }
                    catch (Exception exception) when (
                        IsExpectedFileSystemError(exception))
                    {
                        AddIssue(
                            issues,
                            childDirectory,
                            exception,
                            Classify(exception));
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemError(
                       exception))
            {
                AddIssue(
                    issues,
                    currentDirectory,
                    exception,
                    Classify(exception));
            }
        }

        return new FileScanResult(
            files.OrderBy(
                    file => file.FullPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            issues);
    }

    private void EnumerateFiles(
        string directory,
        List<FileEntry> files,
        List<ScanIssue> issues,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        issues.Add(new ScanIssue(
                            ScanStage.Enumerating,
                            path,
                            ScanIssueCategory.NotFound,
                            "檔案在掃描期間已不存在。"));
                        continue;
                    }

                    if (!DirectoryTraversalRules.CanProcess(info.Attributes))
                    {
                        AddReparsePointIssue(issues, path);
                        continue;
                    }

                    files.Add(new FileEntry(
                        info.FullName,
                        info.Name,
                        info.Extension,
                        info.Length,
                        info.LastWriteTimeUtc));
                    progress?.Report(new ScanProgress(
                        ScanStage.Enumerating,
                        files.Count,
                        0,
                        Percentage: null,
                        CurrentPath: info.FullName));
                }
                catch (Exception exception) when (IsExpectedFileSystemError(
                           exception))
                {
                    AddIssue(
                        issues,
                        path,
                        exception,
                        Classify(exception));
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemError(exception))
        {
            AddIssue(
                issues,
                directory,
                exception,
                Classify(exception));
        }
    }

    private void AddIssue(
        List<ScanIssue> issues,
        string path,
        Exception exception,
        ScanIssueCategory category)
    {
        issues.Add(CreateIssue(path, exception, category));
        LogSkippedPath(logger, path, category.ToString(), exception);
    }

    private void AddReparsePointIssue(
        List<ScanIssue> issues,
        string path)
    {
        issues.Add(new ScanIssue(
            ScanStage.Enumerating,
            path,
            ScanIssueCategory.ReparsePoint,
            "為避免離開所選資料夾或產生循環，已略過連結項目。"));
        LogSkippedPath(
            logger,
            path,
            ScanIssueCategory.ReparsePoint.ToString(),
            null);
    }

    private static ScanIssue CreateIssue(
        string path,
        Exception exception,
        ScanIssueCategory category) =>
        new(
            ScanStage.Enumerating,
            path,
            category,
            category switch
            {
                ScanIssueCategory.AccessDenied => "沒有權限存取此路徑。",
                ScanIssueCategory.NotFound => "路徑在掃描期間已不存在。",
                ScanIssueCategory.PathTooLong => "路徑過長，無法處理。",
                ScanIssueCategory.InvalidInput => "掃描路徑格式無效。",
                _ => "無法讀取此路徑，已略過。"
            });

    private static ScanIssueCategory Classify(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => ScanIssueCategory.AccessDenied,
            DirectoryNotFoundException or FileNotFoundException =>
                ScanIssueCategory.NotFound,
            PathTooLongException => ScanIssueCategory.PathTooLong,
            IOException => ScanIssueCategory.Locked,
            _ => ScanIssueCategory.Unknown
        };

    private static bool IsExpectedFileSystemError(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            PathTooLongException or NotSupportedException or
            System.Security.SecurityException;
}
