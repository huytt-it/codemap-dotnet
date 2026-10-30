namespace CodeMap.Mcp;

/// <summary>
/// Everything the tools tell a model about themselves, in one place. These are the main routing channel: the M0
/// spike showed VS Code does not pass server instructions to the model, so when to call a tool, and when not to,
/// has to be in the tool's own description. Constants because <c>[Description]</c> needs them; English like every
/// other runtime output of this tool.
/// </summary>
internal static class ToolText
{
    /// <summary>Secondary text: clients may not pass it to the model, so the two tool descriptions say the same where it counts.</summary>
    public const string Instructions =
        "CodeMap answers questions about the relationships in C# code from a prebuilt index. To find what a change " +
        "would affect (callers, endpoints, screens, tests), call codemap_impact instead of searching text; call " +
        "codemap_status when a CodeMap tool says the project is not indexed, or to see how fresh the index is.";

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

    public const string Impact =
        "Shows what could be affected by changing a C# method, class or property: the HTTP endpoints, background jobs " +
        "and MediatR handlers that reach it, the frontend screens that call those endpoints, the tests that cover it, a " +
        "risk score, past tickets that touched its file, files that usually change together with it, and the blind " +
        "spots of the analysis. Call it before changing or removing code, and for questions such as 'who calls X', " +
        "'what breaks if I change X' or 'which endpoints use X'. " +
        "Use this instead of text search for callers and impact: text search misses calls that go through interfaces, " +
        "dependency injection, MediatR and virtual overrides, and cannot tell a real caller from a mention. " +
        "Use text search for comments, string literals and config values: CodeMap indexes code relationships, not text. " +
        "The first line says how fresh the index is compared with git HEAD. '0 recognized entry points' does not mean " +
        "no impact: it means the callers found are of a kind CodeMap does not classify yet, so read the sections below " +
        "it and the Blind spots before concluding anything. " +
        "Compact by default; detail=full also lists every intermediate caller and, for a hub, every entry point. " +
        "Read-only; never changes files.";

    public const string ImpactSymbolParam =
        "What to analyze: a symbol docId (such as `M:Shop.Basket.BasketService.AddItem(System.Int32)`) or a " +
        "`Type.Member` name (such as `BasketService.AddItem`). A name that matches several symbols equally well, " +
        "typically overloads, fails with AMBIGUOUS_SYMBOL and lists their docIds: call again with one of them.";

    public const string ImpactDepthParam = "How many levels of callers to follow, 1 to 10. Default 5.";

    public const string ImpactDetailParam =
        "`compact` (default) or `full`. `full` also lists every intermediate caller and replaces a hub's summary with " +
        "its complete entry point list.";

    public const string ProjectParam =
        "Name of an indexed project, as listed in a NOT_INDEXED or AMBIGUOUS_PROJECT message. Omit it to use the " +
        "project the workspace maps to.";
}
