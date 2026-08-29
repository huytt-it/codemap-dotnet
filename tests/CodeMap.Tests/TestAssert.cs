namespace CodeMap.Tests;

/// <summary>Small MSTest-backed helpers for patterns xUnit had built in (predicate Single/Contains, All, Record.Exception) — spec section 2 dependency policy: MSTest only, no xUnit/NUnit.</summary>
internal static class TestAssert
{
    public static T Single<T>(IEnumerable<T> source, Func<T, bool> predicate)
    {
        var matches = source.Where(predicate).ToList();
        Assert.AreEqual(1, matches.Count, $"Expected exactly 1 matching element, found {matches.Count}.");
        return matches[0];
    }

    public static void Contains<T>(IEnumerable<T> source, Func<T, bool> predicate)
        => Assert.IsTrue(source.Any(predicate), "Expected the collection to contain a matching element, found none.");

    public static void DoesNotContain<T>(IEnumerable<T> source, Func<T, bool> predicate)
        => Assert.IsFalse(source.Any(predicate), "Expected the collection to contain no matching element, found one.");

    public static void All<T>(IEnumerable<T> source, Action<T> assertion)
    {
        foreach (var item in source) assertion(item);
    }

    public static Exception? RecordException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
