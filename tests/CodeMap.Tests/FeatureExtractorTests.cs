using CodeMap.Query.FrontendScan;

namespace CodeMap.Tests;

/// <summary>Spec section 6: "feature lấy từ tên thư mục cấp một dưới src/app/, để report gom theo màn hình."</summary>
[TestClass]
public class FeatureExtractorTests
{
    [TestMethod]
    public void Extracts_the_folder_right_after_the_configured_app_dir()
    {
        Assert.AreEqual("orders", FeatureExtractor.Extract("src/app/orders/order-list.component.ts", "src/app"));
    }

    [TestMethod]
    public void Is_case_insensitive_about_the_app_dir_itself()
    {
        Assert.AreEqual("orders", FeatureExtractor.Extract("Src/App/orders/order-list.component.ts", "src/app"));
    }

    [TestMethod]
    public void Falls_back_to_the_first_directory_segment_when_app_dir_is_not_present()
    {
        Assert.AreEqual("legacy", FeatureExtractor.Extract("legacy/order-actions.js", "src/app"));
    }

    [TestMethod]
    public void Falls_back_to_unknown_for_a_file_with_no_directory_at_all()
    {
        Assert.AreEqual("unknown", FeatureExtractor.Extract("order-actions.js", "src/app"));
    }

    [TestMethod]
    public void Honors_a_custom_configured_app_dir()
    {
        Assert.AreEqual("orders", FeatureExtractor.Extract("client/features/orders/list.ts", "client/features"));
    }
}
