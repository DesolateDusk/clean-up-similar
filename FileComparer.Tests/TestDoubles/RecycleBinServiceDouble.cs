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
        string path,
        CancellationToken cancellationToken)
    {
        paths.Add(path);
        return handler(path, cancellationToken);
    }
}
