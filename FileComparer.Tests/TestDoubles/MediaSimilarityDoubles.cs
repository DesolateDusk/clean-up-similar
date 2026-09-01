using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubImageSimilarityVerifier(
    Func<ImageFingerprint, ImageFingerprint, ProcessingResult<ImageSimilarityResult>>
        handler)
    : IImageSimilarityVerifier
{
    private readonly List<(string SourcePath, string TargetPath)> calls = [];

    public IReadOnlyList<(string SourcePath, string TargetPath)> Calls => calls;

    public Task<ProcessingResult<ImageSimilarityResult>> VerifyAsync(
        ImageFingerprint source,
        ImageFingerprint target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add((source.FullPath, target.FullPath));
        return Task.FromResult(handler(source, target));
    }
}

internal sealed class StubVideoFingerprintService(
    IReadOnlyDictionary<string, ProcessingResult<VideoFingerprint>> results)
    : IVideoFingerprintService
{
    private readonly List<(string Path, bool ForceRefresh)> calls = [];

    public IReadOnlyList<(string Path, bool ForceRefresh)> Calls => calls;

    public Task<ProcessingResult<VideoFingerprint>> GetFingerprintAsync(
        FileEntry entry,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add((entry.FullPath, forceRefresh));
        return Task.FromResult(results[entry.FullPath]);
    }
}
