namespace Orders.Legacy;

/// <summary>
/// Review Fix Pass v1, Task 1 fixture: a caller that is deliberately NOT any entry-point type this tool
/// recognizes — not a Controller, not a BackgroundService, not a MediatR handler, just a plain class (stand-in
/// for a Razor Page, Minimal API handler, or any other unrecognized entry point kind). Isolated on purpose:
/// ArchiveService.Archive has no other caller anywhere else in the fixture, so `impact` on it must show
/// 0 recognized entry points with real (non-test) callers still present — the exact "0 entry points" ≠
/// "no impact" case from docs/BENCHMARK-CODEMAP-VS-BASELINE.md's Q2 finding.
/// </summary>
public class LegacyOrderTrigger
{
    public void Run()
    {
        new ArchiveService().Archive();
    }
}

public class ArchiveService
{
    public void Archive()
    {
    }
}
