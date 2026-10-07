namespace Jellyfin.Plugin.DvdAudio;

/// <summary>
/// Path comparisons shared by the importer and the library ignore rule.
/// </summary>
public static class DvdAudioPaths
{
    /// <summary>
    /// Returns true when <paramref name="path"/> is the root or a file inside it.
    /// </summary>
    /// <param name="path">File or directory to test.</param>
    /// <param name="root">Library or source root.</param>
    /// <returns>True when the path is under the root.</returns>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var basePath = Path.GetFullPath(root);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(full, basePath, comparison))
            {
                return true;
            }

            var prefix = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
