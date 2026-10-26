using CodeMap.Cli.Cli;
using static CodeMap.Tests.RegisteredProject;

namespace CodeMap.Tests;

/// <summary>
/// "Could not re-locate this symbol" used to be one message with one explanation — renamed or deleted — for four
/// different situations, three of which are not that. A wrong path (the usual one: the command was started
/// outside the repo) sent the reader off to hunt for a rename that never happened.
/// </summary>
[TestClass]
public class SliceNotFoundTests
{
    [TestMethod]
    public void A_file_that_does_not_exist_names_the_path_that_was_tried_and_suggests_project_or_sync()
    {
        var fx = Build("src", explicitRepo: false);
        var elsewhere = TestPaths.NewTempDir();

        var markdown = RunCommand(SliceCommand.Run, elsewhere, "--index", fx.IndexDir, "--symbol", TargetId);

        var tried = Path.GetFullPath(Path.Combine(elsewhere, "src", "Orders", "Svc.cs"));
        StringAssert.Contains(markdown, tried);
        StringAssert.Contains(markdown, "does not exist");
        StringAssert.Contains(markdown, "--project");
        StringAssert.Contains(markdown, "codemap sync");
        Assert.IsFalse(markdown.Contains("renamed or deleted"), "a missing file says nothing about a rename");
    }

    [TestMethod]
    public void A_file_that_exists_without_the_symbol_says_it_may_have_been_renamed_and_suggests_find()
    {
        var fx = Build("src", explicitRepo: false);
        File.WriteAllText(fx.SolutionFile("Orders/Svc.cs"), SvcSource.Replace("Cancel", "Abort"));

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        StringAssert.Contains(markdown, "Orders/Svc.cs");
        StringAssert.Contains(markdown, "exists");
        StringAssert.Contains(markdown, "renamed");
        StringAssert.Contains(markdown, "codemap find");
        Assert.IsFalse(markdown.Contains("does not exist"), "the file is there; the message must not blame the path");
    }

    [TestMethod]
    public void A_path_outside_the_repo_is_reported_as_such_not_as_a_rename()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", EscapingId);

        StringAssert.Contains(markdown, "outside the repository");
        Assert.IsFalse(markdown.Contains("renamed"));
    }

    [TestMethod]
    public void A_symbol_that_is_not_in_the_index_says_so_instead_of_naming_an_unknown_file()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", "M:No.Such.Symbol");

        StringAssert.Contains(markdown, "not in the index");
        Assert.IsFalse(markdown.Contains("(unknown file)"));
    }

    [TestMethod]
    public void A_symbol_that_is_found_renders_no_miss_message()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        Assert.IsFalse(markdown.Contains("Could not"));
        Assert.IsFalse(markdown.Contains("does not exist"));
    }
}
