using CodeMap.Query.Json;

namespace CodeMap.Query.Config;

/// <summary>
/// Optional `codemap.config.json` at the repo root — lets a repo override conventions the tool can't infer
/// statically (commit ticket ID format, DI-marker attribute name, manual DI overrides for ambiguous types).
/// Absence is normal: every field falls back to a sensible default.
/// </summary>
public sealed class CodeMapConfig
{
    public const string DefaultTicketPattern = @"(?:#|TICKET-|BUG-|JIRA-)(\d{3,6})";
    public const string DefaultFrontendAppDir = "src/app";
    public const string FileName = "codemap.config.json";

    public string? TicketPattern { get; init; }
    public string? DiAttribute { get; init; }
    public Dictionary<string, string>? DiManualOverrides { get; init; }
    public string? FrontendAppDir { get; init; }

    public string EffectiveTicketPattern => string.IsNullOrWhiteSpace(TicketPattern) ? DefaultTicketPattern : TicketPattern;
    public string EffectiveFrontendAppDir => string.IsNullOrWhiteSpace(FrontendAppDir) ? DefaultFrontendAppDir : FrontendAppDir;

    /// <summary>
    /// Reads codemap.config.json, searching <paramref name="startDir"/> and then its parent directories — the
    /// same walk-up ProjectRegistry.Discover already uses. A single directory probe used to be enough only
    /// because callers disagreed on which directory to pass: `scan` passes the solution's directory,
    /// `scan-git` passes the repo root. With a solution nested at `src/App.sln` those are different places, so
    /// one file at the repo root configured `ticketPattern` for scan-git while being invisible to `scan`'s
    /// `diAttribute` lookup. Walking up makes one file at the repo root serve both.
    ///
    /// Throws with a clear message if a file is found but isn't valid JSON — a malformed config should never
    /// be silently ignored.
    /// </summary>
    public static CodeMapConfig Load(string startDir)
    {
        var path = FindUpward(startDir);
        if (path == null) return new CodeMapConfig();

        try
        {
            return JsonUtil.ReadFile<CodeMapConfig>(path) ?? new CodeMapConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse {path}: {ex.Message}", ex);
        }
    }

    private static string? FindUpward(string startDir)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
