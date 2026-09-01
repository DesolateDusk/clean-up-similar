using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using FileComparer.Domain.Rules;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace FileComparer.Infrastructure.Imaging;

public sealed class OpenCvThumbnailService(
    ILogger<OpenCvThumbnailService> logger)
    : IThumbnailService
{
    private static readonly Action<ILogger, string, Exception?>
        LogThumbnailFailure = LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(1311, nameof(LogThumbnailFailure)),
            "Unable to create thumbnail for {Path}");

    public async Task<ProcessingResult<byte[]>> CreateThumbnailAsync(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);

        try
        {
            var bytes = await Task.Run(
                    () => CreateThumbnail(
                        path,
                        maxWidth,
                        maxHeight,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return ProcessingResult.Success(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            LogThumbnailFailure(logger, path, exception);
            return ProcessingResult.Failure<byte[]>(CreateIssue(path, exception));
        }
    }

    private static byte[] CreateThumbnail(
        string path,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = VideoFileRules.IsSupported(Path.GetExtension(path))
            ? ReadVideoFrame(path)
            : Cv2.ImRead(path, ImreadModes.Unchanged);
        if (image.Empty())
        {
            throw new InvalidDataException("OpenCV could not decode the image.");
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                (double)maxWidth / image.Width,
                (double)maxHeight / image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var thumbnail = new Mat();
        Cv2.Resize(
            image,
            thumbnail,
            new Size(width, height),
            interpolation: scale < 1
                ? InterpolationFlags.Area
                : InterpolationFlags.Linear);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Cv2.ImEncode(".png", thumbnail, out var bytes))
        {
            throw new InvalidDataException(
                "OpenCV could not encode the thumbnail.");
        }

        return bytes;
    }

    private static Mat ReadVideoFrame(string path)
    {
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
        {
            throw new InvalidDataException("OpenCV could not open the video.");
        }

        var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            throw new InvalidDataException(
                "OpenCV could not decode a preview video frame.");
        }

        return frame;
    }

    private static ScanIssue CreateIssue(string path, Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.AccessDenied,
                "沒有權限讀取預覽媒體。"),
            FileNotFoundException or DirectoryNotFoundException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.NotFound,
                "預覽媒體已不存在。"),
            PathTooLongException => new ScanIssue(
                ScanStage.Fingerprinting,
                path,
                ScanIssueCategory.PathTooLong,
                "預覽媒體路徑過長。"),
            IOException when (exception is not InvalidDataException) =>
                new ScanIssue(
                    ScanStage.Fingerprinting,
                    path,
                    ScanIssueCategory.Locked,
                "預覽媒體目前無法讀取。"),
            _ => VideoFileRules.IsSupported(Path.GetExtension(path))
                ? new ScanIssue(
                    ScanStage.Fingerprinting,
                    path,
                    ScanIssueCategory.InvalidVideo,
                    "影片無法解碼或建立預覽。")
                : new ScanIssue(
                    ScanStage.Fingerprinting,
                    path,
                    ScanIssueCategory.InvalidImage,
                    "圖片無法解碼或建立預覽。")
        };

    private static bool IsExpectedError(Exception exception) =>
        exception is OpenCVException or InvalidDataException or
            UnauthorizedAccessException or IOException or PathTooLongException or
            NotSupportedException or SecurityException;
}
