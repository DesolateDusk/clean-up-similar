namespace FileComparer.Domain.Models;

public enum CompareMode
{
    ExactFiles,
    SimilarMedia
}

public enum CompareGroupType
{
    ExactDuplicate,
    SimilarImage,
    SimilarVideo
}

public enum CompareItemStatus
{
    Active,
    Recycled,
    DeleteFailed
}

public sealed record FileEntry(
    string FullPath,
    string Name,
    string Extension,
    long Size,
    DateTime LastWriteTimeUtc);

public sealed record FileFingerprint(
    string FullPath,
    long Size,
    string? QuickHash,
    string? Sha256);

public sealed record ImageFingerprint(
    string FullPath,
    int Width,
    int Height,
    double AspectRatio,
    ulong? PerceptualHash,
    IReadOnlyList<ulong>? LocalPerceptualHashes = null)
{
    public IReadOnlyList<ulong> CandidateHashes =>
        LocalPerceptualHashes is { Count: > 0 } hashes
            ? hashes
            : PerceptualHash is ulong hash
                ? [hash]
                : [];
}

public sealed record VideoFingerprint(
    string FullPath,
    TimeSpan Duration,
    int Width,
    int Height,
    string? VideoCodec,
    IReadOnlyList<VideoFrameFingerprint> Frames);

public sealed record VideoFrameFingerprint(
    double PositionRatio,
    TimeSpan Timestamp,
    ulong PerceptualHash,
    double Contrast);

public sealed record ImageSimilarityResult(
    int HashDistance,
    int MatchedFeatureCount,
    double InlierRatio,
    double FinalScore,
    bool IsSimilar);

public sealed record CompareItem(
    FileEntry File,
    ImageFingerprint? Image = null,
    int? HammingDistance = null,
    bool IsSuggestedKeep = false,
    CompareItemStatus Status = CompareItemStatus.Active,
    string? StatusMessage = null,
    VideoFingerprint? Video = null,
    double? SimilarityScore = null);

public sealed record CompareGroup(
    Guid Id,
    CompareGroupType Type,
    IReadOnlyList<CompareItem> Items);
