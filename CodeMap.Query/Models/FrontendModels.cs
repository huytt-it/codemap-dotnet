namespace CodeMap.Query.Models;

/// <summary>frontend-calls.jsonl (spec section 4/6), produced by `scan-fe`. InjectedBy: components whose constructor injects the service this call lives in (Angular DI, one hop only) — empty for jQuery calls (no DI concept) or when no component directly injects the service.</summary>
public sealed record FrontendCall(
    string Id, string File, int Line, string HttpMethod, string RawUrl, string Route, string Feature, string Confidence, List<string> InjectedBy);

/// <summary>api-links.jsonl (spec section 4/6), produced by `link`. MatchKind ∈ exact | ambiguous.</summary>
public sealed record ApiLink(string FrontendId, string BackendId, string MatchKind);
