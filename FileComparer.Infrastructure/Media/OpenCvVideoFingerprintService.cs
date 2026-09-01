using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.ImgHash;

namespace FileComparer.Infrastructure.Media;

/// <summary>
/// Extracts a fixed, cacheable set of video frame pHashes through the existing
/// OpenCV runtime. No external process is started from the application.
/// </summary>
public sealed class OpenCvVideoFingerprintService(
    IVideoFingerprintCache cache,
    IMediaProcessingScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<OpenCvVideoFingerprintService> logger)
    : IVideoFingerprintService
{
    private static readonly Action<ILogger, string, Exception?>
        LogCacheReadFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1321, nameof(LogCacheReadFailure)),
            "Unable to read video fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogCacheWriteFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1322, nameof(LogCacheWriteFailure)),
            "Unable to write video fingerprint cache for {Path}");

    private static readonly Action<ILogger, string, Exception?>
        LogFingerprintFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1323, nameof(LogFingerprintFailure)),
            "Unable to decode or fingerprint video {Path}");

    public async Task<ProcessingResult<VideoFingerprint>> GetFingerprintAsync(
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
                var cached = await cache
                    .GetVideoAsync(entry, cancellationToken)
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
                    "無法讀取影片指紋快取，已改為重新計算。"));
                LogCacheReadFailure(logger, entry.FullPath, exception);
            }
        }

        if (!VideoFileRules.IsSupported(entry.Extension))
        {
            issues.Add(InvalidVideoIssue(entry.FullPath, "不支援此影片格式。"));
            return new ProcessingResult<VideoFingerprint>(null, issues);
        }

        var identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<VideoFingerprint>(null, issues);
        }

        VideoFingerprint computed;
        try
        {
            computed = await scheduler.RunAsync(
                    token => Task.Run(
                        () => ComputeFingerprint(entry, token),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsVideoOrFileError(exception))
        {
            issues.Add(CreateProcessingIssue(entry.FullPath, exception));
            LogFingerprintFailure(logger, entry.FullPath, exception);
            return new ProcessingResult<VideoFingerprint>(null, issues);
        }

        identityIssue = ValidateIdentity(entry);
        if (identityIssue is not null)
        {
            issues.Add(identityIssue);
            return new ProcessingResult<VideoFingerprint>(null, issues);
        }

        try
        {
            await cache.UpsertVideoAsync(
                    CreateCacheRecord(
                        entry,
                        computed,
                        timeProvider.GetUtcNow().UtcDateTime),
                    cancellationToken)
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
                "無法更新影片指紋快取，但本次結果仍可使用。"));
            LogCacheWriteFailure(logger, entry.FullPath, exception);
        }

        return ProcessingResult.Success(computed, issues);
    }

    private static bool TryCreateFromCache(
        VideoCacheRecord? record,
        out VideoFingerprint fingerprint)
    {
        if (record is null ||
            !string.Equals(
                record.FingerprintVersion,
                MediaSimilarityPolicy.VideoFingerprintVersion,
                StringComparison.Ordinal) ||
            record.Frames.Count is < 1 or > MediaSimilarityPolicy.VideoSampleFrameCount ||
            record.Width < 0 ||
            record.Height < 0 ||
            record.DurationMilliseconds < 0)
        {
            fingerprint = null!;
            return false;
        }

        var frames = new List<VideoFrameFingerprint>(record.Frames.Count);
        foreach (var frame in record.Frames.OrderBy(frame => frame.SampleIndex))
        {
            if (frame.PositionRatio is < 0 or > 1 ||
                frame.TimestampMilliseconds < 0 ||
                frame.Contrast < 0 ||
                !ulong.TryParse(
                    frame.PerceptualHash,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var hash))
            {
                fingerprint = null!;
                return false;
            }

            frames.Add(new VideoFrameFingerprint(
                frame.PositionRatio,
                TimeSpan.FromMilliseconds(frame.TimestampMilliseconds),
                hash,
                frame.Contrast));
        }

        fingerprint = new VideoFingerprint(
            record.Path,
            TimeSpan.FromMilliseconds(record.DurationMilliseconds),
            record.Width,
            record.Height,
            record.VideoCodec,
            frames);
        return true;
    }

    private static VideoFingerprint ComputeFingerprint(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var capture = new VideoCapture(entry.FullPath);
        if (!capture.IsOpened())
        {
            throw new InvalidDataException("OpenCV could not open the video.");
        }

        var frameCount = (long)Math.Round(
            capture.Get(VideoCaptureProperties.FrameCount));
        if (frameCount < 1)
        {
            throw new InvalidDataException(
                "OpenCV did not report any readable video frames.");
        }

        var fps = capture.Get(VideoCaptureProperties.Fps);
        var duration = fps > 0
            ? TimeSpan.FromSeconds(frameCount / fps)
            : TimeSpan.Zero;
        var width = Math.Max(
            0,
            (int)Math.Round(capture.Get(VideoCaptureProperties.FrameWidth)));
        var height = Math.Max(
            0,
            (int)Math.Round(capture.Get(VideoCaptureProperties.FrameHeight)));
        var codec = FormatFourCc(capture.Get(VideoCaptureProperties.FourCC));
        var sampledFrameIndexes = new HashSet<long>();
        var frames = new List<VideoFrameFingerprint>(
            MediaSimilarityPolicy.VideoSampleFrameCount);
        using var algorithm = PHash.Create();

        for (var sampleIndex = 0;
             sampleIndex < MediaSimilarityPolicy.VideoSampleFrameCount;
             sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = MediaSimilarityPolicy.VideoSampleFrameCount == 1
                ? 0d
                : sampleIndex /
                  (double)(MediaSimilarityPolicy.VideoSampleFrameCount - 1);
            var requestedFrame = (long)Math.Round(ratio * (frameCount - 1));
            capture.Set(VideoCaptureProperties.PosFrames, requestedFrame);
            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
            {
                continue;
            }

            var actualFrame = Math.Max(
                0,
                (long)Math.Round(capture.Get(VideoCaptureProperties.PosFrames)) - 1);
            if (!sampledFrameIndexes.Add(actualFrame))
            {
                continue;
            }

            using var gray = new Mat();
            if (frame.Channels() == 1)
            {
                frame.CopyTo(gray);
            }
            else
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            }

            using var hashMat = new Mat();
            algorithm.Compute(gray, hashMat);
            if (hashMat.Empty() || hashMat.Total() * hashMat.ElemSize() != 8)
            {
                throw new InvalidDataException(
                    "OpenCV pHash did not return an eight-byte video frame value.");
            }

            hashMat.GetArray(out byte[] hashBytes);
            Cv2.MeanStdDev(gray, out _, out var standardDeviation);
            var timestamp = fps > 0
                ? TimeSpan.FromSeconds(actualFrame / fps)
                : TimeSpan.FromMilliseconds(
                    Math.Max(0, capture.Get(VideoCaptureProperties.PosMsec)));
            frames.Add(new VideoFrameFingerprint(
                ratio,
                timestamp,
                BinaryPrimitives.ReadUInt64LittleEndian(hashBytes),
                standardDeviation.Val0));
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException(
                "OpenCV could not decode a usable video frame.");
        }

        return new VideoFingerprint(
            entry.FullPath,
            duration,
            width,
            height,
            codec,
            frames);
    }

    private static VideoCacheRecord CreateCacheRecord(
        FileEntry entry,
        VideoFingerprint fingerprint,
        DateTime updatedAtUtc) => new(
        entry.FullPath,
        entry.Size,
        entry.LastWriteTimeUtc.Ticks,
        Math.Max(0, (long)Math.Round(fingerprint.Duration.TotalMilliseconds)),
        fingerprint.Width,
        fingerprint.Height,
        fingerprint.VideoCodec,
        MediaSimilarityPolicy.VideoFingerprintVersion,
        fingerprint.Frames
            .Select((frame, index) => new VideoFrameCacheRecord(
                index,
                frame.PositionRatio,
                Math.Max(0, (long)Math.Round(frame.Timestamp.TotalMilliseconds)),
                frame.PerceptualHash.ToString("X16", CultureInfo.InvariantCulture),
                frame.Contrast))
            .ToArray(),
        updatedAtUtc.Ticks);

    private static string? FormatFourCc(double rawValue)
    {
        var value = (int)Math.Round(rawValue);
        if (value == 0)
        {
            return null;
        }

        Span<char> characters = stackalloc char[4];
        for (var index = 0; index < characters.Length; index++)
        {
            var character = (char)((value >> (index * 8)) & 0xFF);
            if (char.IsControl(character))
            {
                return value.ToString("X8", CultureInfo.InvariantCulture);
            }

            characters[index] = character;
        }

        return new string(characters).Trim();
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
                    "影片已不存在。");
            }

            return info.Length == entry.Size &&
                   info.LastWriteTimeUtc.Ticks == entry.LastWriteTimeUtc.Ticks
                ? null
                : new ScanIssue(
                    ScanStage.Fingerprinting,
                    entry.FullPath,
                    ScanIssueCategory.ChangedDuringScan,
                    "影片在掃描期間已變更，已略過本次結果。");
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
                "沒有權限讀取影片。"),
            FileNotFoundException or DirectoryNotFoundException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.NotFound,
                "影片已不存在。"),
            PathTooLongException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.PathTooLong,
                "影片路徑過長。"),
            IOException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.Locked,
                "影片目前無法讀取。"),
            _ => InvalidVideoIssue(path, "影片無法解碼或建立抽偵指紋。")
        };

    private static ScanIssue CacheIssue(string path, string message) =>
        new(
            ScanStage.Fingerprinting,
            path,
            ScanIssueCategory.Cache,
            message);

    private static ScanIssue InvalidVideoIssue(string path, string message) =>
        new(
            ScanStage.Fingerprinting,
            path,
            ScanIssueCategory.InvalidVideo,
            message);

    private static bool IsVideoOrFileError(Exception exception) =>
        exception is OpenCVException or InvalidDataException ||
        IsFileError(exception);

    private static bool IsFileError(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            PathTooLongException or NotSupportedException or SecurityException;
}
