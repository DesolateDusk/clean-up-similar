using FileComparer.Domain.Interfaces;
using FileComparer.Domain.Models;

namespace FileComparer.Tests.TestDoubles;

internal sealed class StubRecycleBinService(
    Func<string, CancellationToken, Task<DeleteOutcome>> handler)
    : IRecycleBinService
{
    private readonly List<string> paths = [];

    public IReadOnlyList<string> Paths => paths;

    public Task<DeleteOutcome> RecycleAsync(
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        var path = entry.FullPath;
        paths.Add(path);
        return handler(path, cancellationToken);
    }
}
