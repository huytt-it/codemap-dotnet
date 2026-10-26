using System.Text.RegularExpressions;
using CodeMap.Query.Models;

namespace CodeMap.Query.Git;

/// <summary>Groups commits by the ticket IDs found in their message (spec section 5, "Trích ticket ID"), applying the mandatory noise filters.</summary>
internal static class TicketExtractor
{
    // The number a hosting service's own merge template stamps into the subject. It identifies the pull request,
    // not the work item, and the default pattern (#\d{3,6}) matches it: on eShopOnWeb 118 of 503 history entries
    // are "Merge pull request #N from ...", so their numbers were reported as tickets (see d773a0d). Only the
    // template is neutralised — a "#1234" a person typed into an ordinary subject is still a ticket, and so is
    // anything in the branch name or in the body. GitHub: "Merge pull request #456 from org/branch".
    // Bitbucket: "Merged in feature/x (pull request #456)".
    private static readonly Regex[] MergeTemplatePullRequestNumbers =
    {
        new(@"(?<=^Merge pull request )#\d+(?= from )", RegexOptions.Compiled),
        new(@"(?<=\(pull request )#\d+(?=\)\s*$)", RegexOptions.Compiled),
    };

    /// <summary>
    /// Every distinct ticket ID the commit mentions, in order of appearance: subject first, then body. A commit
    /// that closes two tickets belongs to both. The subject and the body are matched separately, so a pattern
    /// cannot match across the line break between them.
    /// </summary>
    public static IReadOnlyList<string> FindTicketIds(GitCommit commit, Regex ticketPattern)
    {
        var ids = new List<string>();
        AddMatches(NeutraliseMergeTemplate(commit.Message), ticketPattern, ids);
        AddMatches(commit.Body, ticketPattern, ids);
        return ids;
    }

    private static string NeutraliseMergeTemplate(string subject)
    {
        foreach (var template in MergeTemplatePullRequestNumbers)
            subject = template.Replace(subject, string.Empty);
        return subject;
    }

    private static void AddMatches(string text, Regex ticketPattern, List<string> ids)
    {
        if (text.Length == 0) return;

        foreach (Match match in ticketPattern.Matches(text))
        {
            var id = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
            if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }
    }

    public static List<TicketFileRecord> Extract(
        IReadOnlyList<GitCommit> commits, Regex ticketPattern, int noiseFileThreshold, IReadOnlySet<string> allowedExtensions)
    {
        var byTicket = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var commit in commits)
        {
            var ticketIds = FindTicketIds(commit, ticketPattern);
            if (ticketIds.Count == 0) continue;

            // Bulk commit (merge, mass rename, repo-wide format) — the file list is noise, not a real relationship.
            if (commit.Files.Count > noiseFileThreshold) continue;

            var relevantFiles = commit.Files.Where(f => allowedExtensions.Contains(Path.GetExtension(f))).ToList();
            if (relevantFiles.Count == 0) continue;

            foreach (var ticketId in ticketIds)
            {
                if (!byTicket.TryGetValue(ticketId, out var acc))
                    byTicket[ticketId] = acc = new Accumulator(ticketId);

                acc.Commits.Add(commit.Hash);
                acc.Files.UnionWith(relevantFiles);
                // git log defaults to newest-first, so the first commit seen per ticket is already its latest.
                acc.LatestDate ??= commit.Date;
                acc.LatestMessage ??= commit.Message;
            }
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
