using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubCacheRepository : ICacheRepository
{
    private readonly Dictionary<string, FileCacheRecord> records =
        new(StringComparer.OrdinalIgnoreCase);

    public bool ThrowOnGet { get; init; }

    public bool ThrowOnUpsert { get; init; }

    public int GetCalls { get; private set; }

    public int UpsertCalls { get; private set; }

    public IReadOnlyCollection<FileCacheRecord> Records => records.Values;

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<FileCacheRecord?> GetAsync(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCalls++;

        if (ThrowOnGet)
        {
            throw new InvalidOperationException("cache unavailable");
        }

        records.TryGetValue(entry.FullPath, out var record);
        return Task.FromResult(
            record?.Matches(entry) == true ? record : null);
    }

    public Task UpsertAsync(
        FileCacheRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpsertCalls++;

        if (ThrowOnUpsert)
        {
            throw new InvalidOperationException("cache unavailable");
        }

        records[record.Path] = record;
        return Task.CompletedTask;
    }

    public void Add(FileCacheRecord record) => records[record.Path] = record;
}
