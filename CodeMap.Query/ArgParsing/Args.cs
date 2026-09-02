namespace CodeMap.Query.ArgParsing;

/// <summary>
/// Manual parser for `--name value` / `--flag` style args. No System.CommandLine (API still churning).
/// Every command passes its own whitelist of option/flag names to <see cref="Parse"/> rather than accepting
/// anything shaped like `--x`: a typo (`--projet` instead of `--project`) used to be silently dropped and
/// surface three lines later as an unrelated "Missing --index" error, which is exactly the kind of misleading
/// output this tool exists to prevent in the code it indexes. Whether a name takes a value is now decided by
/// that whitelist, not by peeking at the shape of the next token — so a value that itself starts with `--`
/// (`--query "--foo"`) is taken literally instead of being misread as the next flag.
/// </summary>
internal sealed class Args
{
    /// <summary>Accepted on every command regardless of its own whitelist below. `--verbose` is a cross-cutting
    /// concern read by CliApp.Run's catch-all, not by any individual command, so it would otherwise have to be
    /// added to every single whitelist just to avoid being rejected as unrecognized.</summary>
    private static readonly string[] GlobalFlags = { "verbose" };

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args, IReadOnlyCollection<string> options, IReadOnlyCollection<string> flags)
    {
        var optionNames = new HashSet<string>(options, StringComparer.OrdinalIgnoreCase);
        var flagNames = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
        flagNames.UnionWith(GlobalFlags);

        var result = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException($"Invalid argument: '{token}' (must start with --)");

            var name = token[2..];
            if (optionNames.Contains(name))
            {
                if (i + 1 >= args.Length)
                    throw new CliUsageException($"--{name} requires a value");
                result._options[name] = args[++i];
            }
            else if (flagNames.Contains(name))
            {
                result._flags.Add(name);
            }
            else
            {
                throw new CliUsageException($"Unrecognized argument: '--{name}'");
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
    {
        if (!_options.TryGetValue(name, out var value)) return fallback;
        if (!int.TryParse(value, out var parsed))
            throw new CliUsageException($"--{name} must be an integer, got '{value}'");
        return parsed;
    }

    public bool HasFlag(string name) => _flags.Contains(name);
}
