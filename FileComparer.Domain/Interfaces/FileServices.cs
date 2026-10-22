using FileComparer.Domain.Models;

namespace FileComparer.Domain.Interfaces;

public interface IFileScanner
{
    Task<FileScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IQuickHashService
{
    Task<ProcessingResult<string>> GetQuickHashAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public interface IHashService
{
    Task<ProcessingResult<string>> GetSha256Async(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public interface IImageFingerprintService
{
    Task<ProcessingResult<ImageFingerprint>> GetFingerprintAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public interface IImageCandidateIndex
{
    long CandidateCount { get; }

    void Reset(int maxHammingDistance);

    void Add(ImageFingerprint fingerprint);

    IEnumerable<ImageFingerprint> FindCandidates(ImageFingerprint fingerprint);
}

public interface IImageSimilarityVerifier
{
    Task<ProcessingResult<ImageSimilarityResult>> VerifyAsync(
        ImageFingerprint source,
        ImageFingerprint target,
        CancellationToken cancellationToken);
}

public interface IVideoFingerprintService
{
    Task<ProcessingResult<VideoFingerprint>> GetFingerprintAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public interface IVideoFingerprintCache
{
    Task<VideoCacheRecord?> GetVideoAsync(
        FileEntry entry,
        CancellationToken cancellationToken);

    Task UpsertVideoAsync(
        VideoCacheRecord record,
        CancellationToken cancellationToken);
}

public interface IMediaProcessingScheduler
{
    Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public interface ICacheRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<FileCacheRecord?> GetAsync(
        FileEntry entry,
        CancellationToken cancellationToken);

    Task UpsertAsync(
        FileCacheRecord record,
        CancellationToken cancellationToken);
}

public interface IThumbnailService
{
    Task<ProcessingResult<byte[]>> CreateThumbnailAsync(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken);
}

public interface IFileLauncher
{
    Task<FileActionResult> OpenFileAsync(
        string path,
        CancellationToken cancellationToken);

    Task<FileActionResult> OpenContainingFolderAsync(
        string path,
        CancellationToken cancellationToken);
}

public interface IRecycleBinService
{
    Task<DeleteOutcome> RecycleAsync(
        FileEntry entry,
        CancellationToken cancellationToken);
}

public interface IAppDataPaths
{
    string CacheDatabasePath { get; }

    string LogDirectoryPath { get; }
}
