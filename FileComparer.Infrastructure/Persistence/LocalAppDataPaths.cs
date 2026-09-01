using FileComparer.Domain.Interfaces;

namespace FileComparer.Infrastructure.Persistence;

public sealed class LocalAppDataPaths : IAppDataPaths
{
    public LocalAppDataPaths(string? baseDirectory = null)
    {
        var localApplicationData = baseDirectory ??
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "Windows LocalApplicationData path is unavailable.");
        }

        var applicationRoot = Path.Combine(
            Path.GetFullPath(localApplicationData),
            "FileComparer");
        var cacheDirectory = Path.Combine(applicationRoot, "Cache");
        LogDirectoryPath = Path.Combine(applicationRoot, "Logs");
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(LogDirectoryPath);
        CacheDatabasePath = Path.Combine(cacheDirectory, "file-cache.db");
    }

    public string CacheDatabasePath { get; }

    public string LogDirectoryPath { get; }
}
