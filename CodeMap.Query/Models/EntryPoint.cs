namespace CodeMap.Query.Models;

/// <summary>entrypoints.json (spec section 4). type ∈ http | job | handler | event.</summary>
public sealed record EntryPoint(string Id, string Type, string? HttpMethod = null, string? Route = null);
