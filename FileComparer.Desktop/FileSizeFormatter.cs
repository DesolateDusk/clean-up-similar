using System.Globalization;

namespace FileComparer.Desktop;

internal static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:0.##} {1}",
            value,
            units[unit]);
    }
}
