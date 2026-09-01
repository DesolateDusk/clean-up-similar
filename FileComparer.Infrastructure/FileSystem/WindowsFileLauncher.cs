using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;
using Microsoft.Extensions.Logging;

namespace FileComparer.Infrastructure.FileSystem;

public sealed class WindowsFileLauncher(ILogger<WindowsFileLauncher> logger)
    : IFileLauncher
{
    private static readonly Action<ILogger, string, Exception?> LogOpenFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1111, nameof(LogOpenFailure)),
            "Unable to open shell target {Path}");

    public Task<FileActionResult> OpenFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Failure("此功能只支援 Windows。"));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(Failure("檔案已不存在。"));
        }

        var startInfo = new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = true
        };
        return Task.FromResult(Start(startInfo, path));
    }

    public Task<FileActionResult> OpenContainingFolderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Failure("此功能只支援 Windows。"));
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            LogOpenFailure(logger, path, exception);
            return Task.FromResult(Failure("檔案路徑無效。"));
        }

        if (directory is null || !Directory.Exists(directory))
        {
            return Task.FromResult(Failure("檔案所在資料夾已不存在。"));
        }

        ProcessStartInfo startInfo;
        if (File.Exists(path))
        {
            startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(Path.GetFullPath(path));
        }
        else
        {
            startInfo = new ProcessStartInfo(directory)
            {
                UseShellExecute = true
            };
        }

        return Task.FromResult(Start(startInfo, path));
    }

    private FileActionResult Start(ProcessStartInfo startInfo, string path)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return process is null
                ? Failure("Windows 無法啟動指定項目。")
                : new FileActionResult(IsSuccess: true);
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            LogOpenFailure(logger, path, exception);
            return Failure("Windows 無法開啟指定項目。");
        }
    }

    private static FileActionResult Failure(string message) =>
        new(IsSuccess: false, message);

    private static bool IsExpectedError(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or
            UnauthorizedAccessException or SecurityException or IOException or
            NotSupportedException or ArgumentException;
}
