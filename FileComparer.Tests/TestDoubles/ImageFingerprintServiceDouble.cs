using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubImageFingerprintService(
    IReadOnlyDictionary<string, ProcessingResult<ImageFingerprint>> results)
    : IImageFingerprintService
{
    private readonly List<(string Path, bool ForceRefresh)> calls = [];

    public IReadOnlyList<(string Path, bool ForceRefresh)> Calls => calls;

    public Task<ProcessingResult<ImageFingerprint>> GetFingerprintAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add((entry.FullPath, forceRefresh));
        return Task.FromResult(results[entry.FullPath]);
    }
}
