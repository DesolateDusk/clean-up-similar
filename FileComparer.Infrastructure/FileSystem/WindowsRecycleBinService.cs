using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;

namespace FileComparer.Infrastructure.FileSystem;

public sealed class WindowsRecycleBinService(
    ILogger<WindowsRecycleBinService> logger)
    : IRecycleBinService
{
    private static readonly Action<ILogger, string, Exception?>
        LogRecycleFailure = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1121, nameof(LogRecycleFailure)),
            "Unable to move {Path} to the Windows Recycle Bin");

    public async Task<DeleteOutcome> RecycleAsync(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.FullPath;
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Failure(path, "資源回收筒功能只支援 Windows。");
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return Failure(path, "檔案已不存在。");
            }

            // Reject observable changes between scanning and this operation,
            // including a link or junction introduced along the path.
            if (file.Length != entry.Size ||
                file.LastWriteTimeUtc != entry.LastWriteTimeUtc)
            {
                return Failure(path, "檔案在掃描後已變更，請重新掃描再刪除。");
            }

            for (var current = file.FullName; current is not null;
                 current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return Failure(path, "路徑包含連結項目，請重新掃描再刪除。");
                }
            }

            await Task.Run(
                    () => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException),
                    cancellationToken)
                .ConfigureAwait(false);

            return File.Exists(path)
                ? Failure(path, "Windows 未能將檔案移至資源回收筒。")
                : new DeleteOutcome(path, IsSuccess: true);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return Failure(path, "已取消移至資源回收筒。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            LogRecycleFailure(logger, path, exception);
            return Failure(path, ErrorMessage(exception));
        }
    }

    private static DeleteOutcome Failure(string path, string message) =>
        new(path, IsSuccess: false, message);

    private static string ErrorMessage(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException or SecurityException =>
                "沒有權限將檔案移至資源回收筒。",
            FileNotFoundException or DirectoryNotFoundException =>
                "檔案已不存在。",
            PathTooLongException => "檔案路徑過長。",
            IOException => "檔案目前被占用或無法移至資源回收筒。",
            _ => "Windows 無法將檔案移至資源回收筒。"
        };

    private static bool IsExpectedError(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or
            PathTooLongException or NotSupportedException or SecurityException or
            ArgumentException;
}
