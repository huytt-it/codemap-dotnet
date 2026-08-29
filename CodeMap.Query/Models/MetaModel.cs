namespace CodeMap.Query.Models;

/// <summary>meta.json — written by every `scan*` command; the only source query commands use to compute the staleness banner (spec section 4 & 7.5).</summary>
public sealed class MetaModel
{
    public required string IndexedAt { get; init; }
    public string? GitCommit { get; init; }
    public string? GitBranch { get; init; }
    public required string SolutionPath { get; init; }
    public required int ProjectCount { get; init; }
    public List<string> DegradedProjects { get; init; } = new();
    public required int SymbolCount { get; init; }
    public required int EdgeCount { get; init; }
}
