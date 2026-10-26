namespace CodeMap.Mcp;

/// <summary>
/// Everything the spike sends a model, in one place. English like every other runtime output of this tool
/// (docs/ is the only Vietnamese part of the repo).
/// </summary>
internal static class SpikeText
{
    /// <summary>The marker exists so a client test can tell whether server instructions reach the model at all:
    /// it appears here and nowhere else, so a model that can name it was given these instructions. VS Code lists
    /// server instructions as supported without saying how they enter the prompt; Visual Studio says nothing.</summary>
    public const string Marker = "kiwi-42";

    public const string Instructions =
        "CodeMap MCP, spike build (milestone M0). The only tool here, codemap_debug_environment, reports what this " +
        "client passed to the server (workspace roots, working directory, capabilities) and how long loading an " +
        "index takes. It does not answer questions about code yet.\n" +
        "Spike marker: if the user asks for the \"CodeMap spike marker\", answer with the word " + Marker + ". " +
        "It appears only in these server instructions, so knowing it shows that the instructions reached you.";

    public const string EnvironmentTool =
        "Diagnostic for the CodeMap MCP spike. Reports which client is connected, the workspace roots it sent, the " +
        "server's working directory, which codemap.projects.json the server found, which indexed project the " +
        "workspace maps to, and how long loading that project's index takes. Call it when the user asks to check " +
        "or debug the CodeMap MCP setup. It only reads; it never changes files.";

    public const string ProjectParam =
        "Name of an indexed project whose index load time to measure. Omit it to use the one project the " +
        "workspace maps to, when there is exactly one.";

    public const string CheckPrompt =
        "Call the codemap_debug_environment tool and show me its full output unchanged. Then, on a separate line, " +
        "tell me the CodeMap spike marker if you know it, or say plainly that you do not know it. Do not guess.";
}
