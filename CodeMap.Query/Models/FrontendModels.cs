namespace CodeMap.Query.Models;

/// <summary>frontend-calls.jsonl (spec section 4/6), produced by `scan-fe`. InjectedBy: components whose constructor injects the service this call lives in (Angular DI, one hop only) — empty for jQuery calls (no DI concept) or when no component directly injects the service. BaseUrlDropped: the URL expression began with a non-literal part (`${this.baseUrl}/orders`) that the normalizer discarded, so Route is only the tail of the real path — `link` may then match it as a suffix of a backend route. Absent in files written before the field existed (read as false).</summary>
public sealed record FrontendCall(
    string Id, string File, int Line, string HttpMethod, string RawUrl, string Route, string Feature, string Confidence, List<string> InjectedBy,
    bool BaseUrlDropped = false);

/// <summary>api-links.jsonl (spec section 4/6), produced by `link`. MatchKind ∈ exact | suffix | ambiguous.</summary>
public sealed record ApiLink(string FrontendId, string BackendId, string MatchKind);
