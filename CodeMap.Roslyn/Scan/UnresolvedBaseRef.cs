namespace CodeMap.Roslyn.Scan;

/// <summary>Base type that couldn't be resolved even in the solution-wide merged compilation — goes straight to diagnostics, no guessing.</summary>
internal sealed record UnresolvedBaseRef(string FromDocId, string BaseSimpleName, string Project, string File, int Line);
