using CodeMap.Query.ArgParsing;
using CodeMap.Query.Git;

namespace CodeMap.Query.Config;

/// <summary>
/// Every query command needs one index directory. It can be given the long way (<c>--index &lt;dir&gt;</c>) or by
/// name out of <see cref="ProjectRegistry"/> (<c>--project MyApp</c>). Centralized so the two forms behave
/// identically everywhere, and so the "you gave neither" message is actually useful.
/// </summary>
internal static class IndexPathResolver
{
    /// <summary>Option names every command that calls <see cref="Resolve"/> must include in its own Args.Parse
    /// whitelist — shared here so none of them can forget one and silently break, say, --project.</summary>
    public static readonly string[] OptionNames = { "index", "project", "config" };

    public static string Resolve(Args args) => ResolveLocation(args).IndexDir;

    /// <summary>
    /// Like <see cref="Resolve"/>, plus the repository the index describes when the registry knows it. Commands
    /// that look at the CURRENT state of the repo (the staleness banner, slice's source files) must use
    /// <see cref="IndexLocation.RepoRoot"/> instead of the process's working directory: an MCP server's cwd is
    /// whatever its client launched it in, and nothing says that is the repo.
    /// </summary>
    public static IndexLocation ResolveLocation(Args args)
    {
        var explicitIndex = args.GetOrDefault("index");
        var projectName = args.GetOrDefault("project");

        if (explicitIndex != null && projectName != null)
            throw new CliUsageException("Pass either --index or --project, not both.");

        if (explicitIndex != null) return new IndexLocation(Path.GetFullPath(explicitIndex), RepoRoot: null);

        if (projectName == null)
            throw new CliUsageException(
                $"Missing --index <dir> (or --project <name>, if you have a {ProjectRegistry.FileName}).");

        var registry = ProjectRegistry.Discover(args.GetOrDefault("config"))
            ?? throw new CliUsageException(
                $"--project needs a {ProjectRegistry.FileName}, and none was found in this directory, any parent, or ~/.codemap/. " +
                "Create one (see README) or pass --index <dir> instead.");

        var entry = registry.Require(projectName);
        var indexDir = registry.IndexDirOf(entry);

        if (!Directory.Exists(indexDir))
            throw new CliUsageException(
                $"Project '{projectName}' points at '{indexDir}', which does not exist yet. Run `codemap sync --project {projectName}` first.");

        return new IndexLocation(indexDir, GitRootOrSelf(registry.RepoOf(entry)));
    }

    /// <summary>
    /// <see cref="ProjectRegistry.RepoOf"/> defaults to the solution's own directory, which for <c>src/App.sln</c>
    /// is <c>&lt;root&gt;/src</c>, not the git root. meta.json's solutionPath (and so slice's path arithmetic) is
    /// relative to the git root, so ask git for it; if git cannot answer, the registry's value is the best guess left.
    /// </summary>
    private static string GitRootOrSelf(string repoPath)
        => Path.GetFullPath((Directory.Exists(repoPath) ? GitRepoInfo.TryGetRepoRoot(repoPath) : null) ?? repoPath);
}

/// <param name="IndexDir">The directory holding symbols.jsonl and the rest of the index.</param>
/// <param name="RepoRoot">Absolute repo root for a registered project; null for a bare <c>--index</c>, where the old convention applies: the process is run from inside the repo.</param>
internal sealed record IndexLocation(string IndexDir, string? RepoRoot);
