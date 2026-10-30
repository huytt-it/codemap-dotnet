using ModelContextProtocol;

namespace CodeMap.Mcp;

/// <summary>
/// A failure the model can act on: the workspace is not indexed, two projects match, the index is missing. The SDK
/// returns an <see cref="McpException"/>'s message to the client as a tool error, where the model reads it and
/// retries (with <c>project=</c>, say); any other exception reaches it only as a generic "an error occurred". So the
/// message starts with a stable code and then says what was tried and what to do next, in English like every other
/// runtime output of this tool.
/// </summary>
internal sealed class CodeMapToolException(string code, string detail) : McpException($"{code}: {detail}")
{
    public const string NotIndexed = "NOT_INDEXED";
    public const string AmbiguousProject = "AMBIGUOUS_PROJECT";
    public const string RegistryUnreadable = "REGISTRY_UNREADABLE";
    public const string IndexMissing = "INDEX_MISSING";
    public const string IndexUnreadable = "INDEX_UNREADABLE";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string AmbiguousSymbol = "AMBIGUOUS_SYMBOL";

    public string Code { get; } = code;
}
