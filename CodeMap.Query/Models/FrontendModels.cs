namespace CodeMap.Query.Models;

/// <summary>frontend-calls.jsonl (spec section 4/6), produced by `scan-fe`.</summary>
public sealed record FrontendCall(
    string Id, string File, int Line, string HttpMethod, string RawUrl, string Route, string Feature, string Confidence);

/// <summary>api-links.jsonl (spec section 4/6), produced by `link`. MatchKind ∈ exact | ambiguous.</summary>
public sealed record ApiLink(string FrontendId, string BackendId, string MatchKind);
