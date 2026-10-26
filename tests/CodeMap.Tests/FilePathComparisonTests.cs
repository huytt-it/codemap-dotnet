using CodeMap.Query.Impact;

namespace CodeMap.Tests;

[TestClass]
public class FilePathComparisonTests
{
    [TestMethod]
    public void A_file_below_the_root_is_inside()
    {
        var root = TestPaths.NewTempDir();

        Assert.IsTrue(FilePathComparison.IsInside(root, Path.Combine(root, "src", "Orders", "Svc.cs")));
    }

    [TestMethod]
    public void Dot_dot_segments_are_applied_before_the_check()
    {
        var root = TestPaths.NewTempDir();

        Assert.IsTrue(FilePathComparison.IsInside(root, Path.Combine(root, "src", "..", "Shared", "Util.cs")));
        Assert.IsFalse(FilePathComparison.IsInside(root, Path.Combine(root, "src", "..", "..", "Evil.cs")));
    }

    [TestMethod] // "<root>-other" starts with the text of "<root>" without being below it
    public void A_sibling_directory_sharing_the_roots_name_prefix_is_outside()
    {
        var parent = TestPaths.NewTempDir();
        var root = Path.Combine(parent, "repo");

        Assert.IsFalse(FilePathComparison.IsInside(root, Path.Combine(parent, "repo-other", "Svc.cs")));
    }

    [TestMethod]
    public void A_trailing_separator_on_the_root_makes_no_difference()
    {
        var root = TestPaths.NewTempDir();

        Assert.IsTrue(FilePathComparison.IsInside(root + Path.DirectorySeparatorChar, Path.Combine(root, "Svc.cs")));
    }

    [TestMethod]
    public void The_root_itself_is_not_inside_itself()
    {
        var root = TestPaths.NewTempDir();

        Assert.IsFalse(FilePathComparison.IsInside(root, root));
    }
}
