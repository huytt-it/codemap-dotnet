namespace CodeMap.Query.Map;

/// <summary>Preserves the content between &lt;!-- human:start --&gt; and &lt;!-- human:end --&gt; on regenerate.</summary>
internal static class HumanNotes
{
    private const string StartTag = "<!-- human:start -->";
    private const string EndTag = "<!-- human:end -->";

    public static string? Extract(string? existingContent)
    {
        if (string.IsNullOrEmpty(existingContent)) return null;
        var start = existingContent.IndexOf(StartTag, StringComparison.Ordinal);
        var end = existingContent.IndexOf(EndTag, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start) return null;
        return existingContent[start..(end + EndTag.Length)];
    }

    public static string Block(string? preserved)
        => !string.IsNullOrEmpty(preserved)
            ? preserved
            : $"{StartTag}\n(hand-written notes go here — preserved across regenerate)\n{EndTag}";
}
