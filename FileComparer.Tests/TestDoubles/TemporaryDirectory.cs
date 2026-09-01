namespace FileComparer.Tests.TestDoubles;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        FullPath = Path.Combine(
            Path.GetTempPath(),
            "FileComparer.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string GetPath(string name) => Path.Combine(FullPath, name);

    public void Dispose()
    {
        if (Directory.Exists(FullPath))
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
