using FileComparer.Domain.Interfaces;

namespace FileComparer.Tests.TestDoubles;

internal sealed record StubAppDataPaths(
    string CacheDatabasePath,
    string LogDirectoryPath)
    : IAppDataPaths;
