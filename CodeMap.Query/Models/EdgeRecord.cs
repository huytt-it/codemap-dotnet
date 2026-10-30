namespace CodeMap.Query.Models;

/// <summary>
/// kind ∈ call | new | implements | inherits | read | write. via (optional) says how a call edge was found:
/// "interface" and "override" copy one call to every implementation / override the static type allows,
/// "mediatr" joins mediator.Send/Publish to its handlers by convention, and "delegate" is a method passed as a
/// delegate or event handler (written in the code, but run only when the delegate is invoked).
/// </summary>
public sealed class EdgeRecord
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
    public string? Via { get; init; }
}
