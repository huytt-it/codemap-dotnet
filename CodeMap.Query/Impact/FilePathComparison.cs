namespace CodeMap.Query.Impact;

/// <summary>
/// How two file paths are compared. The file system decides, not the code: Windows (and macOS by default)
/// treats <c>Orders/Svc.cs</c> and <c>orders/svc.cs</c> as one file, so an exact-string join between two sources
/// that spell a path differently (git's index vs the project file the Roslyn scan read) silently loses rows there,
/// while on Linux they really are two files and folding them would join unrelated ones.
/// </summary>
public static class FilePathComparison
{
    public static StringComparison Comparison { get; } = CaseInsensitiveFileSystem() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = CaseInsensitiveFileSystem() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool CaseInsensitiveFileSystem() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// True when <paramref name="path"/> resolves to somewhere below <paramref name="root"/> once both are made
    /// absolute (so <c>..</c> segments are applied first). A directory whose name merely starts with the root's —
    /// <c>/repo-other</c> against <c>/repo</c> — is not inside it.
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, Comparison);
    }
}
