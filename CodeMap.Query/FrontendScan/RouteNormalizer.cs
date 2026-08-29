using System.Text.RegularExpressions;

namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6, "Normalize URL thành route pattern" — the shared rule `link` applies to a backend route
/// template (e.g. "api/orders/{id}", "api/orders/{id:int}") so it can be compared against a normalized frontend
/// URL on equal footing.
/// </summary>
public static partial class RouteNormalizer
{
    public static string NormalizeBackendRoute(string route)
    {
        var withoutParams = RouteParamPattern().Replace(route, "{*}");
        return NormalizeCommon(withoutParams);
    }

    internal static string NormalizeCommon(string s)
    {
        var withoutQuery = s.Split('?')[0];
        return withoutQuery.Trim('/').ToLowerInvariant();
    }

    [GeneratedRegex(@"\{[^}:]+(?::[^}]+)?\}")]
    private static partial Regex RouteParamPattern();
}
