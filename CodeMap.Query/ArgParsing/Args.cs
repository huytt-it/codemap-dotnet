namespace CodeMap.Query.ArgParsing;

/// <summary>Manual parser for `--name value` / `--flag` style args. No System.CommandLine (API still churning).</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var result = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException($"Invalid argument: '{token}' (must start with --)");

            var name = token[2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (hasValue)
            {
                result._options[name] = args[++i];
            }
            else
            {
                result._flags.Add(name);
            }
        }

        return result;
    }

    public string Require(string name)
    {
        if (_options.TryGetValue(name, out var value)) return value;
        throw new CliUsageException($"Missing required argument --{name}");
    }

    public string? GetOrDefault(string name, string? fallback = null)
        => _options.TryGetValue(name, out var value) ? value : fallback;

    public int GetIntOrDefault(string name, int fallback)
        => _options.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public bool HasFlag(string name) => _flags.Contains(name);
}
