using CodeMap.Cli.Cli;
using CodeMap.Query.ArgParsing;
using static CodeMap.Tests.RegisteredProject;

namespace CodeMap.Tests;

/// <summary>
/// What `slice` shows of the live code: how much (--max-lines, default 40) and how sure it is that it found the
/// right declaration. A name-only match used to take the first overload in the file and say nothing about it.
/// </summary>
[TestClass]
public class SliceSnippetTests
{
    private static string LongCancel(int bodyLines)
    {
        var body = string.Join("\n", Enumerable.Range(1, bodyLines).Select(i => $"        var v{i} = {i};"));
        return $"namespace Orders;\npublic class Svc\n{{\n    public void Cancel()\n    {{\n{body}\n    }}\n}}\n";
    }

    [TestMethod]
    public void Snippet_is_cut_at_40_lines_by_default_and_says_how_much_is_missing()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(fx.SolutionFile("Orders/Svc.cs"), LongCancel(bodyLines: 60)); // 63 lines in all

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        StringAssert.Contains(markdown, "var v34 = 34;");
        Assert.IsFalse(markdown.Contains("var v60 = 60;"));
        StringAssert.Contains(markdown, "23 more line(s) truncated");
    }

    [TestMethod]
    public void Max_lines_raises_the_cut()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(fx.SolutionFile("Orders/Svc.cs"), LongCancel(bodyLines: 60));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId, "--max-lines", "100");

        StringAssert.Contains(markdown, "var v60 = 60;");
        Assert.IsFalse(markdown.Contains("truncated"));
    }

    [TestMethod]
    public void Max_lines_can_also_lower_the_cut()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(fx.SolutionFile("Orders/Svc.cs"), LongCancel(bodyLines: 60));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId, "--max-lines", "10");

        StringAssert.Contains(markdown, "var v4 = 4;");
        Assert.IsFalse(markdown.Contains("var v10 = 10;"));
        StringAssert.Contains(markdown, "53 more line(s) truncated");
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("lots")]
    public void Max_lines_must_be_a_positive_integer(string value)
    {
        var fx = Build("src", explicitRepo: false);

        var ex = Assert.ThrowsException<CliUsageException>(() => SliceCommand.Run(
            new[] { "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId, "--max-lines", value }));

        StringAssert.Contains(ex.Message, "--max-lines must be");
    }

    [TestMethod]
    public void A_name_only_match_picks_the_overload_closest_to_the_scanned_line_not_the_first_in_the_file()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(
            fx.SolutionFile("Orders/Svc.cs"),
            "namespace Orders;\npublic class Svc\n{\n    public void Cancel(Missing a) { }\n\n    public void Cancel(Other b) { }\n}\n");
        // The scan saw both parameter types; this file parsed alone cannot, so neither docId can be reproduced.
        fx.AppendSymbol(Symbol("M:Orders.Svc.Cancel(Orders.Other)", "Cancel", "Orders.Svc", "Orders/Svc.cs", 6));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", "M:Orders.Svc.Cancel(Orders.Other)");

        StringAssert.Contains(markdown, "### Orders/Svc.cs:6");
        StringAssert.Contains(markdown, "Cancel(Other b)");
        Assert.IsFalse(markdown.Contains("Cancel(Missing a)"));
    }

    [TestMethod]
    public void A_name_only_match_is_labelled_and_says_how_many_declarations_competed()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(
            fx.SolutionFile("Orders/Svc.cs"),
            "namespace Orders;\npublic class Svc\n{\n    public void Cancel(Missing a) { }\n\n    public void Cancel(Other b) { }\n}\n");
        fx.AppendSymbol(Symbol("M:Orders.Svc.Cancel(Orders.Other)", "Cancel", "Orders.Svc", "Orders/Svc.cs", 6));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", "M:Orders.Svc.Cancel(Orders.Other)");

        StringAssert.Contains(markdown, "Matched by name");
        StringAssert.Contains(markdown, "2 declarations");
        StringAssert.Contains(markdown, "wrong overload");
    }

    [TestMethod]
    public void A_single_name_only_match_is_still_labelled_but_does_not_talk_about_overloads()
    {
        var fx = Build("src", explicitRepo: false);
        // Svc.cs has exactly one Cancel(), but the index records one taking an int: only a name match can find it.
        fx.AppendSymbol(Symbol("M:Orders.Svc.Cancel(System.Int32)", "Cancel", "Orders.Svc", "Orders/Svc.cs", 4));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", "M:Orders.Svc.Cancel(System.Int32)");

        StringAssert.Contains(markdown, "Matched by name");
        Assert.IsFalse(markdown.Contains("overload"));
    }

    [TestMethod]
    public void An_exact_docId_match_carries_no_fuzzy_label()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        StringAssert.Contains(markdown, "### Orders/Svc.cs:4");
        Assert.IsFalse(markdown.Contains("Matched by name"));
    }
}
