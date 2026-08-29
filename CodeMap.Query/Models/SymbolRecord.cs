namespace CodeMap.Query.Models;

public sealed class SymbolRecord
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? ContainingType { get; init; }
    public required string Project { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
    public required string Accessibility { get; init; }
    public List<string> Attributes { get; init; } = new();
}
