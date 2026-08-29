namespace CodeMap.Roslyn.Scan;

internal sealed class ParsedProject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string Directory { get; init; }
    public required bool IsSdkStyle { get; init; }
    public required List<string> CompileFiles { get; init; }
    public required List<string> ProjectReferences { get; init; }
}
