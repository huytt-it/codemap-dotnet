namespace CodeMap.Mcp;

/// <summary>
/// Turns an MCP root (a <c>file://</c> URI) into a local directory path, and answers "is this path inside that
/// one" the way a file system on this OS would.
/// </summary>
internal static class RootPath
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Null for anything that is not a local file URI. Decodes the path by hand instead of trusting
    /// <see cref="Uri.LocalPath"/>: VS Code sends the drive colon percent-encoded (<c>file:///d%3A/Repos/Shop</c>),
    /// a shape that is easy to get subtly wrong, and this keeps the rule in one visible place.
    /// </summary>
    public static string? FromUri(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) return null;

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!string.IsNullOrEmpty(uri.Host))
            path = $"//{uri.Host}{path}"; // file://server/share/... is a UNC path
        else if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..]; // "/d:/Repos" -> "d:/Repos"

        return Normalize(path);
    }

    /// <summary>Full path, OS separators, no trailing separator (except on a bare root such as <c>C:\</c>).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool IsSameOrInside(string path, string directory)
    {
        var p = Normalize(path);
        var d = Normalize(directory);
        if (string.Equals(p, d, PathComparison)) return true;

        var prefix = d.EndsWith(Path.DirectorySeparatorChar) ? d : d + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, PathComparison);
    }
}
