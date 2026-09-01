using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubQuickHashService(
    IReadOnlyDictionary<string, ProcessingResult<string>> results)
    : IQuickHashService
{
    private readonly Dictionary<string, int> callCounts =
        new(StringComparer.OrdinalIgnoreCase);

    public int TotalCalls => callCounts.Values.Sum();

    public int CallsFor(string path) =>
        callCounts.GetValueOrDefault(path);

    public Task<ProcessingResult<string>> GetQuickHashAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        callCounts[entry.FullPath] = CallsFor(entry.FullPath) + 1;
        return Task.FromResult(results[entry.FullPath]);
    }
}

internal sealed class StubHashService(
    IReadOnlyDictionary<string, ProcessingResult<string>> results)
    : IHashService
{
    private readonly Dictionary<string, int> callCounts =
        new(StringComparer.OrdinalIgnoreCase);

    public int TotalCalls => callCounts.Values.Sum();

    public int CallsFor(string path) =>
        callCounts.GetValueOrDefault(path);

    public Task<ProcessingResult<string>> GetSha256Async(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        callCounts[entry.FullPath] = CallsFor(entry.FullPath) + 1;
        return Task.FromResult(results[entry.FullPath]);
    }
}
