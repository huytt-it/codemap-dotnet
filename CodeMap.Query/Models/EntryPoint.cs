namespace CodeMap.Query.Models;

/// <summary>
/// entrypoints.json (spec section 4). type ∈ http | job | handler | event.
/// For http: HttpMethod is GET/POST/... or "ANY" for an action with no verb constraint (ASP.NET accepts every
/// verb there). Route is null when an attribute's template couldn't be resolved to a constant. RouteInferred is
/// true when no attribute declared the route and it was derived from the framework's default convention
/// ({controller}/{action}/{id?}, or api/{controller}/{id?} for Web API 2) — the app's real route table may
/// differ; absent (null) when an attribute declared it.
/// </summary>
public sealed record EntryPoint(string Id, string Type, string? HttpMethod = null, string? Route = null, bool? RouteInferred = null);
