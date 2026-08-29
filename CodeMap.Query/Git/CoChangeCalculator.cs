using CodeMap.Query.Models;

namespace CodeMap.Query.Git;

/// <summary>Counts how often pairs of files change together across commits (spec section 4, co-change.jsonl).</summary>
internal static class CoChangeCalculator
{
    public static List<CoChangeRecord> Compute(
        IReadOnlyList<GitCommit> commits, int noiseFileThreshold, IReadOnlySet<string> allowedExtensions, int minTogether)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairCounts = new Dictionary<(string A, string B), int>();

        foreach (var commit in commits)
        {
            // Same noise rule as TicketExtractor: a bulk commit poisons every pair it touches.
            if (commit.Files.Count > noiseFileThreshold) continue;

            var files = commit.Files
                .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            foreach (var f in files)
                totals[f] = totals.GetValueOrDefault(f) + 1;

            for (var i = 0; i < files.Count; i++)
            for (var j = i + 1; j < files.Count; j++)
            {
                var key = (files[i], files[j]);
                pairCounts[key] = pairCounts.GetValueOrDefault(key) + 1;
            }
        }

        var result = new List<CoChangeRecord>();
        foreach (var ((fileA, fileB), together) in pairCounts)
        {
            if (together < minTogether) continue;

            var totalA = totals[fileA];
            var totalB = totals[fileB];
            var strength = Math.Round((double)together / Math.Min(totalA, totalB), 2);
            result.Add(new CoChangeRecord(fileA, fileB, together, totalA, totalB, strength));
        }

        return result
            .OrderByDescending(r => r.Strength)
            .ThenBy(r => r.FileA, StringComparer.Ordinal)
            .ThenBy(r => r.FileB, StringComparer.Ordinal)
            .ToList();
    }
}
