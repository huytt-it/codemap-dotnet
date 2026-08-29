namespace CodeMap.Query.Models;

/// <summary>ticket-files.jsonl — every ticket ID found in commit messages, and which files its commits touched.</summary>
public sealed record TicketFileRecord(string Ticket, List<string> Commits, string Date, string Message, List<string> Files);

/// <summary>co-change.jsonl — two files that keep changing together, a relationship static analysis can't see (reflection, MediatR, FE/BE boundary, stored procedures). strength = together / min(totalA, totalB).</summary>
public sealed record CoChangeRecord(string FileA, string FileB, int Together, int TotalA, int TotalB, double Strength);
