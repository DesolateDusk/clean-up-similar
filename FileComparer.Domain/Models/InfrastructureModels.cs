namespace FileComparer.Domain.Models;

public sealed record FileCacheRecord(
    string Path,
    long Size,
    long LastWriteTimeUtcTicks,
    string? QuickHash,
    string? Sha256,
    int? Width,
    int? Height,
    string? PerceptualHash,
    long UpdatedAtUtcTicks,
    string? ImageCandidateHashes = null)
{
    public bool Matches(FileEntry entry) =>
        string.Equals(Path, entry.FullPath, StringComparison.OrdinalIgnoreCase) &&
        Size == entry.Size &&
        LastWriteTimeUtcTicks == entry.LastWriteTimeUtc.Ticks;

    public static FileCacheRecord Create(FileEntry entry, DateTime updatedAtUtc) =>
        new(
            entry.FullPath,
            entry.Size,
            entry.LastWriteTimeUtc.Ticks,
            null,
            null,
            null,
            null,
            null,
            updatedAtUtc.Ticks);
}

public sealed record VideoFrameCacheRecord(
    int SampleIndex,
    double PositionRatio,
    long TimestampMilliseconds,
    string PerceptualHash,
    double Contrast);

public sealed record VideoCacheRecord(
    string Path,
    long Size,
    long LastWriteTimeUtcTicks,
    long DurationMilliseconds,
    int Width,
    int Height,
    string? VideoCodec,
    string FingerprintVersion,
    IReadOnlyList<VideoFrameCacheRecord> Frames,
    long UpdatedAtUtcTicks)
{
    public bool Matches(FileEntry entry) =>
        string.Equals(Path, entry.FullPath, StringComparison.OrdinalIgnoreCase) &&
        Size == entry.Size &&
        LastWriteTimeUtcTicks == entry.LastWriteTimeUtc.Ticks;
}

public sealed record ProcessingResult<T>(
    T? Value,
    IReadOnlyList<ScanIssue> Issues)
    where T : class
{
    public bool IsSuccess => Value is not null;
}

public static class ProcessingResult
{
    public static ProcessingResult<T> Success<T>(
        T value,
        IReadOnlyList<ScanIssue>? issues = null)
        where T : class =>
        new(value, issues ?? Array.Empty<ScanIssue>());

    public static ProcessingResult<T> Failure<T>(ScanIssue issue)
        where T : class =>
        new(default, new[] { issue });
}

public sealed record FileActionResult(
    bool IsSuccess,
    string? ErrorMessage = null);

public sealed record DeleteSummary(
    int Count,
    long TotalBytes);

public sealed record DeleteOutcome(
    string Path,
    bool IsSuccess,
    string? ErrorMessage = null);

public sealed record DeleteBatchResult(
    DeleteSummary Summary,
    IReadOnlyList<DeleteOutcome> Outcomes,
    IReadOnlyList<CompareGroup> UpdatedGroups,
    bool IsCancelled = false);
