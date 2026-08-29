namespace CodeMap.Query.Git;

internal sealed record GitCommit(string Hash, string Date, string Message, List<string> Files);
