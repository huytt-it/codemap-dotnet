namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6: "feature lấy từ tên thư mục cấp một dưới src/app/ (hoặc tương đương), để report gom theo
/// màn hình." <paramref name="appDir"/> is configurable via codemap.config.json's frontendAppDir (default
/// "src/app") for repos that don't follow the Angular CLI convention.
/// </summary>
public static class FeatureExtractor
{
    public static string Extract(string relativeFilePath, string appDir)
    {
        var fileParts = relativeFilePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var appParts = appDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        var matchIndex = FindSubsequence(fileParts, appParts);
        if (matchIndex >= 0)
        {
            var afterApp = matchIndex + appParts.Length;
            if (afterApp < fileParts.Length - 1) // needs at least one dir segment before the filename
                return fileParts[afterApp];
        }

        // Fallback: no recognizable app dir — use the first directory segment, or "unknown" for a file with no directory at all.
        return fileParts.Length > 1 ? fileParts[0] : "unknown";
    }

    private static int FindSubsequence(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.OrdinalIgnoreCase)) { match = false; break; }
            }

            if (match) return i;
        }

        return -1;
    }
}
