namespace CodeMap.Query.ArgParsing;

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}
