using System.Text.RegularExpressions;
using CodeMap.Query.Models;

namespace CodeMap.Query.Git;

/// <summary>Groups commits by the ticket ID found in their message (spec section 5, "Trích ticket ID"), applying the mandatory noise filters.</summary>
internal static class TicketExtractor
{
    public static List<TicketFileRecord> Extract(
        IReadOnlyList<GitCommit> commits, Regex ticketPattern, int noiseFileThreshold, IReadOnlySet<string> allowedExtensions)
    {
        var byTicket = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var commit in commits)
        {
            var match = ticketPattern.Match(commit.Message);
            if (!match.Success) continue;

            // Bulk commit (merge, mass rename, repo-wide format) — the file list is noise, not a real relationship.
            if (commit.Files.Count > noiseFileThreshold) continue;

            var ticketId = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
            var relevantFiles = commit.Files.Where(f => allowedExtensions.Contains(Path.GetExtension(f))).ToList();
            if (relevantFiles.Count == 0) continue;

            if (!byTicket.TryGetValue(ticketId, out var acc))
                byTicket[ticketId] = acc = new Accumulator(ticketId);

            acc.Commits.Add(commit.Hash);
            acc.Files.UnionWith(relevantFiles);
            // git log defaults to newest-first, so the first commit seen per ticket is already its latest.
            acc.LatestDate ??= commit.Date;
            acc.LatestMessage ??= commit.Message;
        }

        return byTicket.Values
            .Select(a => new TicketFileRecord(
                a.TicketId, a.Commits, a.LatestDate!, a.LatestMessage!,
                a.Files.OrderBy(f => f, StringComparer.Ordinal).ToList()))
            .OrderBy(t => t.Ticket, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class Accumulator(string ticketId)
    {
        public string TicketId { get; } = ticketId;
        public List<string> Commits { get; } = new();
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public string? LatestDate { get; set; }
        public string? LatestMessage { get; set; }
    }
}
