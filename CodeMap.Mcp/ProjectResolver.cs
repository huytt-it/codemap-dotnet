using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;

namespace CodeMap.Mcp;

/// <summary>Where the workspace directory came from.</summary>
internal enum WorkspaceSource { WorkspaceArgument, WorkingDirectory }

/// <summary>
/// Why a project was picked. After <see cref="ProjectParameter"/>, the workspace rules are listed from most to least
/// specific; the resolver uses the first rule that any registry entry satisfies.
/// </summary>
internal enum MatchRule
{
    /// <summary>The tool call named the project.</summary>
    ProjectParameter,

    /// <summary>The workspace is the entry's solution folder or inside it.</summary>
    InsideSolutionDir,

    /// <summary>The workspace is the entry's repo root (as git reports it) or inside it.</summary>
    InsideRepoRoot,

    /// <summary>The workspace is the entry's frontend folder or inside it.</summary>
    InsideFrontend,

    /// <summary>The workspace is a folder above the entry's repo root.</summary>
    ContainsRepoRoot,
}

/// <param name="Path">Full path, no trailing separator.</param>
/// <param name="Warnings">Why <c>--workspace</c> was not used, when it was given and could not be.</param>
internal sealed record Workspace(string Path, WorkspaceSource Source, IReadOnlyList<string> Warnings)
{
    public string SourceLabel => Source == WorkspaceSource.WorkspaceArgument ? "--workspace" : "cwd";
}

/// <param name="RepoRoot">The git root, worked out the way the CLI does it (<see cref="IndexPathResolver.GitRootOrSelf"/>), so the banner and source reads agree with `codemap impact --project`.</param>
internal sealed record ResolvedProject(
    ProjectRegistry Registry, ProjectEntry Entry, string IndexDir, string RepoRoot, MatchRule Rule, Workspace Workspace)
{
    public string Name => Entry.Name;
}

/// <summary>
/// Works out which indexed project a tool call is about, without asking the client. MCP roots, which the first
/// design relied on, are deprecated as of specification 2026-07-28 (SEP-2577) and VS Code does not send them, so
/// the inputs are, in order: the tool's <c>project</c> parameter, the server's <c>--workspace</c> argument
/// (VS Code substitutes <c>${workspaceFolder}</c> there), and the process working directory (which VS Code sets
/// to the workspace, but which is the client's choice).
///
/// The registry is found from the workspace: <c>--config</c>, else <c>codemap.projects.json</c> in the workspace or
/// any parent, else <c>~/.codemap</c>. Not from the cwd when <c>--workspace</c> is given: a registry only the cwd
/// can see describes some other tree. The registry is re-read when its timestamp or size changes, so editing it does
/// not need an editor restart; the workspace match and the git repo roots are kept until then.
///
/// One instance lives as long as the server, and tool calls may arrive concurrently.
/// </summary>
internal sealed class ProjectResolver
{
    private static readonly MatchRule[] WorkspaceRules =
        { MatchRule.InsideSolutionDir, MatchRule.InsideRepoRoot, MatchRule.InsideFrontend, MatchRule.ContainsRepoRoot };

    private readonly string? _configArg;
    private readonly string _currentDirectory;
    private readonly string _homeDirectory;
    private readonly object _gate = new();
    private RegistrySnapshot? _snapshot;

    /// <param name="homeDirectory">Where <c>.codemap/codemap.projects.json</c> is looked for last; the user profile in production.</param>
    public ProjectResolver(string? configArg, string? workspaceArg, string currentDirectory, string homeDirectory)
    {
        _configArg = configArg;
        _currentDirectory = Normalize(currentDirectory);
        _homeDirectory = homeDirectory;
        Workspace = ChooseWorkspace(workspaceArg, _currentDirectory);
    }

    public Workspace Workspace { get; }

    /// <summary>The project for a tool call. Throws <see cref="CodeMapToolException"/> (NOT_INDEXED, AMBIGUOUS_PROJECT, REGISTRY_UNREADABLE) with a message the model can act on.</summary>
    public ResolvedProject Resolve(string? project)
    {
        lock (_gate)
        {
            var snapshot = CurrentRegistry();
            var registry = snapshot.Registry;

            if (!string.IsNullOrWhiteSpace(project))
            {
                var named = registry.Projects.FirstOrDefault(p => string.Equals(p.Name, project.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new CodeMapToolException(CodeMapToolException.NotIndexed,
                        $"no project named '{project.Trim()}' in {registry.SourcePath}. Indexed projects: {Names(registry)}. " +
                        "Pass one of these as project=<name>, or omit project to use the one the workspace maps to.");
                return Resolved(snapshot, named, MatchRule.ProjectParameter);
            }

            snapshot.WorkspaceMatches ??= Match(snapshot);
            var matches = snapshot.WorkspaceMatches;
            return matches.Count switch
            {
                1 => Resolved(snapshot, matches[0].Entry, matches[0].Rule),
                0 => throw NotMatched(registry),
                _ => throw Ambiguous(registry, matches),
            };
        }
    }

    private ResolvedProject Resolved(RegistrySnapshot snapshot, ProjectEntry entry, MatchRule rule)
        => new(snapshot.Registry, entry, snapshot.Registry.IndexDirOf(entry), snapshot.RepoRootOf(entry), rule, Workspace);

    /// <summary>The first rule, most specific first, that any entry satisfies; among nested folders under that rule, the deepest.</summary>
    private List<Candidate> Match(RegistrySnapshot snapshot)
    {
        foreach (var rule in WorkspaceRules)
        {
            var hits = new List<Candidate>();
            foreach (var entry in snapshot.Registry.Projects)
            {
                var anchor = AnchorOf(snapshot, entry, rule);
                if (anchor == null) continue;

                var matches = rule == MatchRule.ContainsRepoRoot
                    ? IsSameOrInside(anchor, Workspace.Path)
                    : IsSameOrInside(Workspace.Path, anchor);
                if (matches) hits.Add(new Candidate(entry, rule, anchor));
            }

            if (hits.Count == 0) continue;

            // Every anchor here contains the workspace, so they are nested in one another and the longest path is the
            // innermost: a tools solution under the main solution's folder is what a workspace inside it is about.
            // A workspace above several repos has no such order; those stay ambiguous.
            if (rule != MatchRule.ContainsRepoRoot)
            {
                var deepest = hits.Max(h => h.Anchor.Length);
                hits.RemoveAll(h => h.Anchor.Length != deepest);
            }

            return hits;
        }

        return new List<Candidate>();
    }

    private static string? AnchorOf(RegistrySnapshot snapshot, ProjectEntry entry, MatchRule rule) => rule switch
    {
        MatchRule.InsideSolutionDir => Normalize(Path.GetDirectoryName(snapshot.Registry.ResolvePath(entry.Solution))!),
        MatchRule.InsideRepoRoot or MatchRule.ContainsRepoRoot => snapshot.RepoRootOf(entry),
        MatchRule.InsideFrontend => entry.Frontend == null ? null : Normalize(snapshot.Registry.ResolvePath(entry.Frontend)),
        _ => null,
    };

    /// <summary>The registry as it is on disk now: found again on every call (a few File.Exists), parsed again only when it changed.</summary>
    private RegistrySnapshot CurrentRegistry()
    {
        var (path, how) = FindRegistry();
        var file = new FileInfo(path);
        var stamp = (file.LastWriteTimeUtc, file.Length);
        if (_snapshot != null && string.Equals(_snapshot.Path, path, FilePathComparison.Comparison) && _snapshot.Stamp == stamp)
            return _snapshot;

        ProjectRegistry registry;
        try
        {
            registry = ProjectRegistry.LoadFrom(path);
        }
        catch (CliUsageException ex)
        {
            throw new CodeMapToolException(CodeMapToolException.RegistryUnreadable,
                $"{ex.Message} (registry found via {how}). Fix the file; until then CodeMap cannot answer, so use normal search.");
        }

        return _snapshot = new RegistrySnapshot(path, stamp, registry);
    }

    private (string Path, string How) FindRegistry()
    {
        if (!string.IsNullOrWhiteSpace(_configArg))
        {
            var full = Path.GetFullPath(_configArg, _currentDirectory);
            if (!File.Exists(full))
                throw new CodeMapToolException(CodeMapToolException.RegistryUnreadable,
                    $"--config points at {full}, which does not exist. Fix the path in the server's args.");
            return (full, "--config");
        }

        for (var dir = new DirectoryInfo(Workspace.Path); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ProjectRegistry.FileName);
            if (File.Exists(candidate)) return (candidate, "the workspace or a parent of it");
        }

        var home = Path.Combine(_homeDirectory, ".codemap", ProjectRegistry.FileName);
        if (File.Exists(home)) return (home, "~/.codemap");

        throw new CodeMapToolException(CodeMapToolException.NotIndexed,
            $"no {ProjectRegistry.FileName} for workspace '{Workspace.Path}' (from {Workspace.SourceLabel}): no --config, " +
            $"none in that folder or any parent, none in ~/.codemap. CodeMap has not indexed this code; use normal search." +
            WarningsSuffix());
    }

    private CodeMapToolException NotMatched(ProjectRegistry registry)
    {
        var hint = Workspace.Source == WorkspaceSource.WorkingDirectory
            ? " The workspace is the server's working directory, which the client chooses; if that is not the folder open " +
              "in the editor, add \"--workspace\", \"${workspaceFolder}\" to the server's args in .vscode/mcp.json."
            : "";
        return new CodeMapToolException(CodeMapToolException.NotIndexed,
            $"workspace '{Workspace.Path}' (from {Workspace.SourceLabel}) is not inside any project in {registry.SourcePath}. " +
            $"Indexed projects: {Names(registry)}. Pass project=<name> if the code you are asking about is one of these; " +
            "otherwise use normal search." + hint + WarningsSuffix());
    }

    private CodeMapToolException Ambiguous(ProjectRegistry registry, List<Candidate> matches)
    {
        var why = matches[0].Rule switch
        {
            MatchRule.InsideFrontend => $"is a frontend paired with {matches.Count} backends",
            MatchRule.ContainsRepoRoot => $"is a parent folder of {matches.Count} projects' repositories",
            MatchRule.InsideRepoRoot => $"is inside a repository that holds {matches.Count} projects",
            _ => $"is inside the solution folder of {matches.Count} projects",
        };
        var choices = matches.Select(m =>
            $"\n- {m.Entry.Name}{(string.IsNullOrWhiteSpace(m.Entry.Description) ? "" : $": {m.Entry.Description}")} " +
            $"(solution {registry.ResolvePath(m.Entry.Solution)})");
        return new CodeMapToolException(CodeMapToolException.AmbiguousProject,
            $"workspace '{Workspace.Path}' (from {Workspace.SourceLabel}) {why} in {registry.SourcePath}. " +
            $"Call again with project=<name> for the one you are asking about:{string.Concat(choices)}");
    }

    private string WarningsSuffix() => string.Concat(Workspace.Warnings.Select(w => " Note: " + w));

    private static string Names(ProjectRegistry registry)
        => registry.Projects.Count == 0 ? "(none)" : string.Join(", ", registry.Projects.Select(p => p.Name));

    private static Workspace ChooseWorkspace(string? workspaceArg, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceArg))
            return new Workspace(currentDirectory, WorkspaceSource.WorkingDirectory, Array.Empty<string>());

        // A client that does not substitute variables in its MCP config hands the placeholder over verbatim.
        if (workspaceArg.Contains("${", StringComparison.Ordinal))
            return Fallback($"--workspace '{workspaceArg}' arrived unexpanded (the client did not substitute the variable), so the server's working directory is used.");

        string full;
        try
        {
            full = Normalize(Path.GetFullPath(workspaceArg, currentDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fallback($"--workspace '{workspaceArg}' is not a valid path ({ex.Message}), so the server's working directory is used.");
        }

        return Directory.Exists(full)
            ? new Workspace(full, WorkspaceSource.WorkspaceArgument, Array.Empty<string>())
            : Fallback($"--workspace '{full}' does not exist, so the server's working directory is used.");

        Workspace Fallback(string warning) => new(currentDirectory, WorkspaceSource.WorkingDirectory, new[] { warning });
    }

    /// <summary>Full path, OS separators, no trailing separator except on a bare root such as <c>C:\</c>.</summary>
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrInside(string path, string directory)
        => string.Equals(path, directory, FilePathComparison.Comparison) || FilePathComparison.IsInside(directory, path);

    private sealed record Candidate(ProjectEntry Entry, MatchRule Rule, string Anchor);

    /// <summary>One parse of the registry file and everything worked out from it, dropped together when the file changes.</summary>
    private sealed class RegistrySnapshot(string path, (DateTime, long) stamp, ProjectRegistry registry)
    {
        private readonly Dictionary<string, string> _repoRoots = new(FilePathComparison.Comparer);

        public string Path { get; } = path;
        public (DateTime, long) Stamp { get; } = stamp;
        public ProjectRegistry Registry { get; } = registry;

        /// <summary>The workspace's matches, computed on first use; the workspace never changes for one server.</summary>
        public List<Candidate>? WorkspaceMatches { get; set; }

        /// <summary>One `git rev-parse` per repo per registry version, not per call.</summary>
        public string RepoRootOf(ProjectEntry entry)
        {
            var configured = Registry.RepoOf(entry);
            if (!_repoRoots.TryGetValue(configured, out var root))
                _repoRoots[configured] = root = Normalize(IndexPathResolver.GitRootOrSelf(configured));
            return root;
        }
    }
}
