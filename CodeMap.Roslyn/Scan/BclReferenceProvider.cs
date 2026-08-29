using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// Supplies BCL references from the runtime that is currently running CodeMap (local read, no MSBuild, no
/// network), so the ad-hoc L1 compilation can resolve basic types (int, string, Exception, ...) and produce
/// correct docIds. Does not attempt to resolve the target solution's real package/project references — that's L2's job.
/// </summary>
internal static class BclReferenceProvider
{
    private static ImmutableArray<MetadataReference>? _cache;

    public static ImmutableArray<MetadataReference> GetReferences()
    {
        if (_cache is { } cached) return cached;

        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        var refs = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(TryCreateReference)
            .Where(r => r != null)
            .Select(r => r!)
            .ToImmutableArray();

        _cache = refs;
        return refs;
    }

    private static MetadataReference? TryCreateReference(string path)
    {
        try
        {
            return MetadataReference.CreateFromFile(path);
        }
        catch
        {
            return null;
        }
    }
}
