namespace CodeMap.Query.Impact;

/// <summary>Who reads the report: the command line, or a model calling an MCP tool.</summary>
public enum RenderStyle
{
    /// <summary>The markdown banner opens the report and hints name CLI flags (`--full`, `--depth`). Byte-for-byte what `codemap impact` has always printed.</summary>
    Cli,

    /// <summary>No banner (the tool opens with its own one-line freshness) and hints name the tool's parameters (`detail=full`, `depth`).</summary>
    Mcp,
}

/// <summary>How a report is rendered. Only the style so far; the design's budgets and file:line additions come when real output shows they are needed.</summary>
public sealed record RenderOptions(RenderStyle Style = RenderStyle.Cli);
