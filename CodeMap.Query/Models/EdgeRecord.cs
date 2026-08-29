namespace CodeMap.Query.Models;

/// <summary>kind ∈ call | new | implements | inherits | read | write. via: "interface" | "mediatr" (optional).</summary>
public sealed class EdgeRecord
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
    public string? Via { get; init; }
}
