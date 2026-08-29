using CodeMap.Query.Git;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Roslyn.Scan;

/// <summary>Writes meta.json — shared by SyntaxOnlyScanner (L1) and SemanticScanner (L2), spec section 4.</summary>
internal static class MetaWriter
{
    public static void Write(
        string indexDir, string solutionPath, string solutionDir,
        int projectCount, List<string> degradedProjects, int symbolCount, int edgeCount)
    {
        var repoRoot = GitRepoInfo.TryGetRepoRoot(solutionDir) ?? solutionDir;
        var solutionRelativePath = Path.GetRelativePath(repoRoot, solutionPath).Replace(Path.DirectorySeparatorChar, '/');

        var meta = new MetaModel
        {
            IndexedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            GitCommit = GitRepoInfo.TryGetHeadCommit(solutionDir),
            GitBranch = GitRepoInfo.TryGetBranch(solutionDir),
            SolutionPath = solutionRelativePath,
            ProjectCount = projectCount,
            DegradedProjects = degradedProjects,
            SymbolCount = symbolCount,
            EdgeCount = edgeCount,
        };

        JsonUtil.WriteIndented(Path.Combine(indexDir, "meta.json"), meta);
    }
}
