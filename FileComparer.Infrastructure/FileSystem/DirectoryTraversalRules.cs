namespace FileComparer.Infrastructure.FileSystem;

public static class DirectoryTraversalRules
{
    public static bool CanProcess(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0;
}
