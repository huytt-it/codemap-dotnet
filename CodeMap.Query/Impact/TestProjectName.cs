namespace CodeMap.Query.Impact;

/// <summary>
/// Whether a project name looks like a test project. The index has no IsTestProject flag (the scan would have to
/// read the csproj), so this works from the name — by segment, because a substring test calls `Attestation` and
/// `LatestNews` test projects. A segment is a dot-separated part of the name:
///   * it is Test, Tests, UnitTests, IntegrationTests or FunctionalTests (any case), or
///   * it ends in a capitalised Test/Tests (`OrderServiceTests`) — case-sensitive, so `Contests` does not match.
/// </summary>
internal static class TestProjectName
{
    private static readonly string[] WholeSegments = { "Test", "Tests", "UnitTests", "IntegrationTests", "FunctionalTests" };

    public static bool IsTestProject(string project)
        => project.Split('.', StringSplitOptions.RemoveEmptyEntries).Any(IsTestSegment);

    private static bool IsTestSegment(string segment)
        => WholeSegments.Any(w => segment.Equals(w, StringComparison.OrdinalIgnoreCase))
           || segment.EndsWith("Tests", StringComparison.Ordinal)
           || segment.EndsWith("Test", StringComparison.Ordinal);
}
