using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Extensions.Logging;

namespace FileComparer.Infrastructure.Hashing;

/// <summary>
/// Calculates quick and authoritative hashes with bounded buffers. Cache
/// failures are warnings; file identity changes invalidate the operation.
/// </summary>
public sealed class FileHashService(
    ICacheRepository cacheRepository,
    TimeProvider timeProvider,
    ILogger<FileHashService> logger)
    : IQuickHashService, IHashService
{
    private const int SampleSize = 64 * 1024;

    private static readonly Action<ILogger, string, Exception?>
        LogCacheReadFailure = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1201, nameof(LogCacheReadFailure)),
            "Unable to read fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogCacheWriteFailure = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1202, nameof(LogCacheWriteFailure)),
            "Unable to write fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogHashFailure = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1203, nameof(LogHashFailure)),
            "Unable to hash {Path}");

    public Task<ProcessingResult<string>> GetQuickHashAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        GetHashAsync(
            entry,
            forceRefresh,
            HashKind.Quick,
            cancellationToken);

    public Task<ProcessingResult<string>> GetSha256Async(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        GetHashAsync(
            entry,
            forceRefresh,
            HashKind.Full,
            cancellationToken);

    private async Task<ProcessingResult<string>> GetHashAsync(
        FileEntry entry,
        bool forceRefresh,
        HashKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var issues = new List<ScanIssue>();

        if (!forceRefresh)
        {
            try
            {
                var cached = await cacheRepository
                    .GetAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                var cachedValue = kind == HashKind.Quick
                    ? cached?.QuickHash
                    : cached?.Sha256;
                if (cachedValue is not null)
                {
                    var cachedIdentityIssue = ValidateIdentity(entry);
                    if (cachedIdentityIssue is not null)
                    {
                        issues.Add(cachedIdentityIssue);
                        return new ProcessingResult<string>(null, issues);
                    }

                    return ProcessingResult.Success(cachedValue);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                issues.Add(CacheIssue(entry.FullPath, "無法讀取快取，已重新計算。"));
                LogCacheReadFailure(logger, entry.FullPath, exception);
            }
        }

        var identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<string>(null, issues);
        }

        string hash;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = SampleSize,
                Options = FileOptions.Asynchronous |
                          FileOptions.SequentialScan
            };
            await using var stream = new FileStream(entry.FullPath, options);
            var bytes = kind == HashKind.Quick
                ? await ComputeQuickHashAsync(
                        stream,
                        entry.Size,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            hash = Convert.ToHexString(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileError(exception))
        {
            issues.Add(CreateFileIssue(entry.FullPath, exception));
            LogHashFailure(logger, entry.FullPath, exception);
            return new ProcessingResult<string>(null, issues);
        }

        identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<string>(null, issues);
        }

        var record = FileCacheRecord.Create(
            entry,
            timeProvider.GetUtcNow().UtcDateTime);
        record = kind == HashKind.Quick
            ? record with { QuickHash = hash }
            : record with { Sha256 = hash };

        try
        {
            await cacheRepository
                .UpsertAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            issues.Add(CacheIssue(entry.FullPath, "無法更新快取，本次結果仍可使用。"));
            LogCacheWriteFailure(logger, entry.FullPath, exception);
        }

        return ProcessingResult.Success(hash, issues);
    }

    private static async Task<byte[]> ComputeQuickHashAsync(
        FileStream stream,
        long length,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(SampleSize);

        try
        {
            if (length <= SampleSize * 2L)
            {
                await AppendUntilEndAsync(
                        hash,
                        stream,
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await AppendBytesAsync(
                        hash,
                        stream,
                        buffer,
                        SampleSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                stream.Seek(-SampleSize, SeekOrigin.End);
                await AppendBytesAsync(
                        hash,
                        stream,
                        buffer,
                        SampleSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task AppendUntilEndAsync(
        IncrementalHash hash,
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            hash.AppendData(buffer, 0, read);
        }
    }

    private static async Task AppendBytesAsync(
        IncrementalHash hash,
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream
                .ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, count - totalRead)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            totalRead += read;
        }
    }

    private static ScanIssue? ValidateIdentity(FileEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.FullPath);
            if (!info.Exists)
            {
                return new ScanIssue(
                    ScanStage.Fingerprinting,
                    entry.FullPath,
                    ScanIssueCategory.NotFound,
                    "檔案在雜湊前已不存在。");
            }

            return info.Length == entry.Size &&
                   (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   info.LastWriteTimeUtc.Ticks ==
                   entry.LastWriteTimeUtc.Ticks
                ? null
                : new ScanIssue(
                    ScanStage.Fingerprinting,
                    entry.FullPath,
                    ScanIssueCategory.ChangedDuringScan,
                    "檔案在掃描期間已變更，已略過本次結果。");
        }
        catch (Exception exception) when (IsExpectedFileError(exception))
        {
            return CreateFileIssue(entry.FullPath, exception);
        }
    }

    private static ScanIssue CacheIssue(string path, string message) =>
        new(
            ScanStage.Fingerprinting,
            path,
            ScanIssueCategory.Cache,
            message);

    private static ScanIssue CreateFileIssue(
        string path,
        Exception exception) =>
        new(
            ScanStage.Fingerprinting,
            path,
            exception switch
            {
                UnauthorizedAccessException => ScanIssueCategory.AccessDenied,
                FileNotFoundException or DirectoryNotFoundException =>
                    ScanIssueCategory.NotFound,
                PathTooLongException => ScanIssueCategory.PathTooLong,
                IOException => ScanIssueCategory.Locked,
                _ => ScanIssueCategory.Unknown
            },
            exception switch
            {
                UnauthorizedAccessException => "沒有權限讀取檔案。",
                FileNotFoundException or DirectoryNotFoundException =>
                    "檔案在掃描期間已不存在。",
                PathTooLongException => "檔案路徑過長。",
                _ => "檔案無法讀取，可能正被其他程式使用。"
            });

    private static bool IsExpectedFileError(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            PathTooLongException or NotSupportedException or
            System.Security.SecurityException;

    private enum HashKind
    {
        Quick,
        Full
    }
}
