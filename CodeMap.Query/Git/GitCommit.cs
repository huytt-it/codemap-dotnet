namespace CodeMap.Query.Git;

/// <param name="Message">The subject line only. This is also what ticket-files.jsonl shows as a ticket's message, so it must stay one short line.</param>
/// <param name="Body">Everything after the subject (empty when there is none). Read for ticket IDs, never displayed.</param>
internal sealed record GitCommit(string Hash, string Date, string Message, List<string> Files, string Body = "");
