using CodeMap.Query.ArgParsing;

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

    public static string Resolve(Args args)
    {
        var explicitIndex = args.GetOrDefault("index");
        var projectName = args.GetOrDefault("project");

        if (explicitIndex != null && projectName != null)
            throw new CliUsageException("Pass either --index or --project, not both.");

        if (explicitIndex != null) return Path.GetFullPath(explicitIndex);

        if (projectName == null)
            throw new CliUsageException(
                $"Missing --index <dir> (or --project <name>, if you have a {ProjectRegistry.FileName}).");

        var registry = ProjectRegistry.Discover(args.GetOrDefault("config"))
            ?? throw new CliUsageException(
                $"--project needs a {ProjectRegistry.FileName}, and none was found in this directory, any parent, or ~/.codemap/. " +
                "Create one (see README) or pass --index <dir> instead.");

        var indexDir = registry.IndexDirOf(registry.Require(projectName));

        if (!Directory.Exists(indexDir))
            throw new CliUsageException(
                $"Project '{projectName}' points at '{indexDir}', which does not exist yet. Run `codemap sync --project {projectName}` first.");

        return indexDir;
    }
}
