using CodeMap.Query.ArgParsing;
using CodeMap.Query.Json;

namespace CodeMap.Query.Config;

/// <summary>
/// One entry in <see cref="ProjectRegistry"/>: everything the pipeline needs to (re)build one codebase's index,
/// so a person or an agent never has to remember paths again.
/// </summary>
public sealed class ProjectEntry
{
    /// <summary>Short handle used on the command line: `codemap sync --project MyApp`. Matched case-insensitively.</summary>
    public required string Name { get; init; }

    /// <summary>Free text for whoever reads this file next — including an AI agent, which has no other way to know what this codebase is.</summary>
    public string? Description { get; init; }

    /// <summary>Path to the .sln or .slnx to scan.</summary>
    public required string Solution { get; init; }

    /// <summary>Output directory, the same thing `scan --out` takes. The index itself lands in <c>&lt;Output&gt;/index</c> and MAP.md in <c>&lt;Output&gt;/MAP.md</c>.</summary>
    public required string Output { get; init; }

    /// <summary>Git repo root for `scan-git`. Optional — defaults to the solution's own directory, which is the usual layout.</summary>
    public string? Repo { get; init; }

    /// <summary>Angular/TypeScript frontend root for `scan-fe`. Omit when there is no separate frontend; the FE steps are then skipped rather than guessed at.</summary>
    public string? Frontend { get; init; }

    /// <summary>Language the team writes commits/tickets in (e.g. "ja", "vi", "en"). Purely informational — it tells an agent which language to phrase `where` queries in, which is the difference between the strongest ranking signal firing and not firing at all.</summary>
    public string? CommitLanguage { get; init; }
}

/// <summary>
/// Optional `codemap.projects.json` — one file listing every codebase you index, so multi-project setups stop
/// being "remember four absolute paths per repo". Two readers, deliberately:
///   * the CLI, for `codemap projects` / `codemap sync --project X` / `--project X` on any query command;
///   * an AI agent, which can read this file directly (it cannot run `codemap`) to learn where an index lives
///     and what codebase it covers, instead of being told a hardcoded path that is wrong on the next machine.
/// Every path may be relative — resolved against the config file's own directory, so the file stays valid when
/// the whole tree is cloned somewhere else.
/// </summary>
public sealed class ProjectRegistry
{
    public const string FileName = "codemap.projects.json";

    /// <summary>Ignored by the tool, present so the file explains itself to whoever (or whatever) opens it.</summary>
    public string? Description { get; init; }

    public List<ProjectEntry> Projects { get; init; } = new();

    /// <summary>Directory of the file this registry was loaded from — the base for resolving relative paths.</summary>
    public string BaseDir { get; private set; } = Directory.GetCurrentDirectory();

    /// <summary>Full path of the file this was loaded from, for error messages that tell the user which file to fix.</summary>
    public string SourcePath { get; private set; } = "";

    /// <summary>
    /// Finds the registry: an explicit <c>--config</c> path, else <c>codemap.projects.json</c> walking up from the
    /// current directory (so it works from anywhere inside a repo), else <c>~/.codemap/codemap.projects.json</c>
    /// for people who keep one registry for every repo on the machine. Returns null when there is none — the
    /// registry is optional and every command still works with explicit paths.
    /// </summary>
    public static ProjectRegistry? Discover(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
                throw new CliUsageException($"Config file not found: {full}");
            return LoadFrom(full);
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return LoadFrom(candidate);
            dir = dir.Parent;
        }

        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codemap", FileName);
        return File.Exists(home) ? LoadFrom(home) : null;
    }

    public static ProjectRegistry LoadFrom(string path)
    {
        ProjectRegistry registry;
        try
        {
            registry = JsonUtil.ReadFile<ProjectRegistry>(path)
                       ?? throw new CliUsageException($"{path} is empty.");
        }
        catch (Exception ex) when (ex is not CliUsageException)
        {
            throw new CliUsageException($"Failed to parse {path}: {ex.Message}");
        }

        registry.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        registry.SourcePath = Path.GetFullPath(path);

        var duplicate = registry.Projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new CliUsageException($"{path}: duplicate project name '{duplicate.Key}' — names must be unique, they are how --project selects one.");

        return registry;
    }

    public ProjectEntry Require(string name)
    {
        var entry = Projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry != null) return entry;

        var known = Projects.Count == 0 ? "(none defined)" : string.Join(", ", Projects.Select(p => p.Name));
        throw new CliUsageException($"No project named '{name}' in {SourcePath}. Known projects: {known}");
    }

    /// <summary>Resolves one of the entry's paths against the config file's directory. Absolute paths pass through unchanged.</summary>
    public string ResolvePath(string path) => Path.GetFullPath(Path.Combine(BaseDir, path));

    /// <summary>`scan --out` takes the output dir; every query command takes the `index` subdirectory inside it.</summary>
    public string IndexDirOf(ProjectEntry entry) => Path.Combine(ResolvePath(entry.Output), "index");

    /// <summary>`scan-git --repo` — the entry's own value, or the solution's directory, which is the normal layout.</summary>
    public string RepoOf(ProjectEntry entry) =>
        entry.Repo != null ? ResolvePath(entry.Repo) : Path.GetDirectoryName(ResolvePath(entry.Solution))!;
}
