namespace FileComparer.Domain.Rules;

public static class ImageFileRules
{
    private static readonly HashSet<string> Extensions = new(
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" },
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string? extension) =>
        extension is not null && Extensions.Contains(extension);
}
