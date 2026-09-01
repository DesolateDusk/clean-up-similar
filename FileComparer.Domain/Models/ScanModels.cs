namespace FileComparer.Domain.Models;

public enum ScanStage
{
    Idle,
    Enumerating,
    Fingerprinting,
    Grouping,
    Deleting,
    Completed,
    Cancelled,
    Failed
}

public enum ScanStatus
{
    Completed,
    Cancelled,
    Failed
}

public enum ScanIssueCategory
{
    AccessDenied,
    NotFound,
    Locked,
    PathTooLong,
    ChangedDuringScan,
    InvalidImage,
    InvalidVideo,
    Cache,
    Shell,
    Delete,
    InvalidInput,
    ReparsePoint,
    Unknown
}

public sealed record ScanRequest(
    string RootDirectory,
    bool IncludeSubdirectories,
    CompareMode Mode,
    bool ForceRefresh = false)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(RootDirectory))
        {
            errors.Add("請先選擇掃描目錄。");
        }

        return errors;
    }
}

public sealed record ScanProgress(
    ScanStage Stage,
    int DiscoveredCount,
    int ProcessedCount,
    double? Percentage = null,
    string? CurrentPath = null);

public sealed record ScanIssue(
    ScanStage Stage,
    string? Path,
    ScanIssueCategory Category,
    string Message);

public sealed record FileScanResult(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<ScanIssue> Issues);

public sealed record ScanResult(
    ScanStatus Status,
    IReadOnlyList<CompareGroup> Groups,
    IReadOnlyList<ScanIssue> Issues,
    int DiscoveredCount,
    int ProcessedCount,
    string? ErrorMessage = null);

public sealed class CompareOperationContext
{
    private readonly Action<ScanIssue>? issueReporter;

    public CompareOperationContext(
        ScanRequest request,
        IReadOnlyCollection<FileEntry> files,
        IProgress<ScanProgress>? progress = null,
        Action<ScanIssue>? issueReporter = null)
    {
        Request = request;
        Files = files;
        Progress = progress;
        this.issueReporter = issueReporter;
    }

    public ScanRequest Request { get; }

    public IReadOnlyCollection<FileEntry> Files { get; }

    public IProgress<ScanProgress>? Progress { get; }

    public void ReportIssue(ScanIssue issue) => issueReporter?.Invoke(issue);
}
