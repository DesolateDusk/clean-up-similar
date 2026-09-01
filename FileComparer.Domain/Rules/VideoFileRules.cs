namespace FileComparer.Domain.Rules;

public static class VideoFileRules
{
    private static readonly HashSet<string> Extensions = new(
        new[] { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm" },
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string? extension) =>
        extension is not null && Extensions.Contains(extension);
}
