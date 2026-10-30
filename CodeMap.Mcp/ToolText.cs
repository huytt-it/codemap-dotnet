namespace CodeMap.Mcp;

/// <summary>
/// Everything the tools tell a model about themselves, in one place. These are the main routing channel: the M0
/// spike showed VS Code does not pass server instructions to the model, so when to call a tool, and when not to,
/// has to be in the tool's own description. Constants because <c>[Description]</c> needs them; English like every
/// other runtime output of this tool.
/// </summary>
internal static class ToolText
{
    public const string Status =
        "Reports whether CodeMap covers the current workspace and how far its index can be trusted right now: which " +
        "indexed project the workspace maps to, how far the index is behind git HEAD, what it contains, and the exact " +
        "`codemap sync` command when it needs rebuilding. Call it when another codemap tool reports NOT_INDEXED, " +
        "AMBIGUOUS_PROJECT or INDEX_MISSING, when the user asks whether CodeMap is set up, or before relying on " +
        "CodeMap for a large change. " +
        "CodeMap is a static index built from Roslyn and git history. It is trustworthy for C# call relationships, " +
        "HTTP routes, DI registrations and edit history (tickets, files changed together). It does not cover " +
        "reflection, assembly scanning, stored procedures, runtime behavior, environment-specific configuration or " +
        "data in the database: when a question depends on those, say plainly that CodeMap cannot answer it. " +
        "Read-only; never changes files.";

    public const string ProjectParam =
        "Name of an indexed project, as listed in a NOT_INDEXED or AMBIGUOUS_PROJECT message. Omit it to use the " +
        "project the workspace maps to.";
}
