using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.ImgHash;

namespace FileComparer.Infrastructure.Imaging;

public sealed class OpenCvImageFingerprintService(
    ICacheRepository cacheRepository,
    TimeProvider timeProvider,
    ILogger<OpenCvImageFingerprintService> logger)
    : IImageFingerprintService
{
    private static readonly RotateFlags[] CandidateRotations =
    [
        RotateFlags.Rotate90Clockwise,
        RotateFlags.Rotate180,
        RotateFlags.Rotate90Counterclockwise
    ];

    private static readonly double[] CandidateCropScales = [0.9d, 0.8d, 0.6d];
    private static readonly double[] CandidateCropPositions = [0d, 0.5d, 1d];

    private static readonly Action<ILogger, string, Exception?>
        LogCacheReadFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1301, nameof(LogCacheReadFailure)),
            "Unable to read image fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogCacheWriteFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1302, nameof(LogCacheWriteFailure)),
            "Unable to write image fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogFingerprintFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1303, nameof(LogFingerprintFailure)),
            "Unable to decode or fingerprint image {Path}");

    public async Task<ProcessingResult<ImageFingerprint>> GetFingerprintAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<ScanIssue>();

        if (!forceRefresh)
        {
            try
            {
                var cached = await cacheRepository
                    .GetAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                if (TryCreateFromCache(cached, out var fingerprint))
                {
                    return ProcessingResult.Success(fingerprint);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                issues.Add(CacheIssue(
                    entry.FullPath,
                    "無法讀取圖片指紋快取，已改為重新計算。"));
                LogCacheReadFailure(logger, entry.FullPath, exception);
            }
        }

        if (!ImageFileRules.IsSupported(entry.Extension))
        {
            issues.Add(InvalidImageIssue(
                entry.FullPath,
                "不支援此圖片格式。"));
            return new ProcessingResult<ImageFingerprint>(null, issues);
        }

        var identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<ImageFingerprint>(null, issues);
        }

        ImageFingerprint computed;
        try
        {
            computed = await Task.Run(
                    () => ComputeFingerprint(entry, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsImageOrFileError(exception))
        {
            issues.Add(CreateProcessingIssue(entry.FullPath, exception));
            LogFingerprintFailure(logger, entry.FullPath, exception);
            return new ProcessingResult<ImageFingerprint>(null, issues);
        }

        identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<ImageFingerprint>(null, issues);
        }

        var record = FileCacheRecord.Create(
            entry,
            timeProvider.GetUtcNow().UtcDateTime) with
        {
            Width = computed.Width,
            Height = computed.Height,
            PerceptualHash = computed.PerceptualHash?.ToString(
                "X16",
                CultureInfo.InvariantCulture),
            ImageCandidateHashes = FormatCandidateHashes(
                computed.CandidateHashes)
        };

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
            issues.Add(CacheIssue(
                entry.FullPath,
                "無法更新圖片指紋快取，但本次結果仍可使用。"));
            LogCacheWriteFailure(logger, entry.FullPath, exception);
        }

        return ProcessingResult.Success(computed, issues);
    }

    private static bool TryCreateFromCache(
        FileCacheRecord? record,
        out ImageFingerprint fingerprint)
    {
        if (record?.Width is > 0 &&
            record.Height is > 0 &&
            ulong.TryParse(
                record.PerceptualHash,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var hash) &&
            TryParseCandidateHashes(
                record.ImageCandidateHashes,
                out var candidateHashes))
        {
            fingerprint = new ImageFingerprint(
                record.Path,
                record.Width.Value,
                record.Height.Value,
                (double)record.Width.Value / record.Height.Value,
                hash,
                candidateHashes);
            return true;
        }

        fingerprint = null!;
        return false;
    }

    private static ImageFingerprint ComputeFingerprint(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Cv2.ImRead(entry.FullPath, ImreadModes.Grayscale);
        if (image.Empty())
        {
            throw new InvalidDataException("OpenCV could not decode the image.");
        }

        using var algorithm = PHash.Create();
        var candidateHashes = CreateCandidateHashes(
            image,
            algorithm,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new ImageFingerprint(
            entry.FullPath,
            image.Width,
            image.Height,
            (double)image.Width / image.Height,
            candidateHashes[0],
            candidateHashes);
    }

    private static ulong[] CreateCandidateHashes(
        Mat image,
        PHash algorithm,
        CancellationToken cancellationToken)
    {
        var hashes = new List<ulong>();
        AddHash(image, algorithm, hashes, cancellationToken);

        foreach (var rotation in CandidateRotations)
        {
            using var rotated = new Mat();
            Cv2.Rotate(image, rotated, rotation);
            AddHash(rotated, algorithm, hashes, cancellationToken);
        }

        // The 90% crop also offsets a modest uniform border without making a
        // separate border detector part of candidate generation.
        foreach (var cropScale in CandidateCropScales)
        {
            var cropWidth = Math.Max(
                1,
                (int)Math.Round(image.Width * cropScale));
            var cropHeight = Math.Max(
                1,
                (int)Math.Round(image.Height * cropScale));
            foreach (var horizontal in CandidateCropPositions)
            {
                foreach (var vertical in CandidateCropPositions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var x = (int)Math.Round(
                        (image.Width - cropWidth) * horizontal);
                    var y = (int)Math.Round(
                        (image.Height - cropHeight) * vertical);
                    using var crop = new Mat(
                        image,
                        new Rect(x, y, cropWidth, cropHeight));
                    AddHash(crop, algorithm, hashes, cancellationToken);
                }
            }
        }

        var distinct = hashes.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new InvalidDataException(
                "OpenCV pHash did not return an eight-byte value.");
        }

        return distinct;
    }

    private static void AddHash(
        Mat image,
        PHash algorithm,
        List<ulong> hashes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hashMat = new Mat();
        algorithm.Compute(image, hashMat);
        if (hashMat.Empty() || hashMat.Total() * hashMat.ElemSize() != 8)
        {
            throw new InvalidDataException(
                "OpenCV pHash did not return an eight-byte value.");
        }

        hashMat.GetArray(out byte[] hashBytes);
        hashes.Add(BinaryPrimitives.ReadUInt64LittleEndian(hashBytes));
    }

    private static bool TryParseCandidateHashes(
        string? value,
        out IReadOnlyList<ulong> hashes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            hashes = [];
            return false;
        }

        var parsed = new List<ulong>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ulong.TryParse(
                    part,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var hash))
            {
                hashes = [];
                return false;
            }

            parsed.Add(hash);
        }

        hashes = parsed.Distinct().ToArray();
        return hashes.Count > 0;
    }

    private static string FormatCandidateHashes(
        IEnumerable<ulong> hashes) => string.Join(
        ';',
        hashes
            .Distinct()
            .Select(hash => hash.ToString("X16", CultureInfo.InvariantCulture)));

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
                    "圖片已不存在。");
            }

            return info.Length == entry.Size &&
                   info.LastWriteTimeUtc.Ticks == entry.LastWriteTimeUtc.Ticks
                ? null
                : new ScanIssue(
                    ScanStage.Fingerprinting,
                    entry.FullPath,
                    ScanIssueCategory.ChangedDuringScan,
                    "圖片在掃描期間已變更，已略過本次結果。");
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return CreateProcessingIssue(entry.FullPath, exception);
        }
    }

    private static ScanIssue CreateProcessingIssue(
        string path,
        Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.AccessDenied,
                "沒有權限讀取圖片。"),
            FileNotFoundException or DirectoryNotFoundException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.NotFound,
                "圖片已不存在。"),
            PathTooLongException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.PathTooLong,
                "圖片路徑過長。"),
            IOException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.Locked,
                "圖片目前無法讀取。"),
            _ => InvalidImageIssue(path, "圖片無法解碼或建立 pHash。")
        };

    private static ScanIssue CacheIssue(string path, string message) =>
        new(
            ScanStage.Fingerprinting,
            path,
            ScanIssueCategory.Cache,
            message);

    private static ScanIssue InvalidImageIssue(string path, string message) =>
        new(
            ScanStage.Fingerprinting,
            path,
            ScanIssueCategory.InvalidImage,
            message);

    private static bool IsImageOrFileError(Exception exception) =>
        exception is OpenCVException or InvalidDataException ||
        IsFileError(exception);

    private static bool IsFileError(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            PathTooLongException or NotSupportedException or SecurityException;
}
