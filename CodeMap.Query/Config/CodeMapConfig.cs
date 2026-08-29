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

    /// <summary>Reads codemap.config.json from <paramref name="repoRoot"/> if present. Throws with a clear message if the file exists but isn't valid JSON — a malformed config should never be silently ignored.</summary>
    public static CodeMapConfig Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, FileName);
        if (!File.Exists(path)) return new CodeMapConfig();

        try
        {
            return JsonUtil.ReadFile<CodeMapConfig>(path) ?? new CodeMapConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse {path}: {ex.Message}", ex);
        }
    }
}
