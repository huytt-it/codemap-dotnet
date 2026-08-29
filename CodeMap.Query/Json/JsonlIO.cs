using System.Text;
using System.Text.Json;

namespace CodeMap.Query.Json;

internal static class JsonlWriter
{
    public static void Write<T>(string path, IEnumerable<T> items)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var item in items)
            sw.WriteLine(JsonSerializer.Serialize(item, JsonUtil.Compact));
    }
}

internal static class JsonlReader
{
    public static List<T> Read<T>(string path)
    {
        var result = new List<T>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var obj = JsonSerializer.Deserialize<T>(line, JsonUtil.Compact);
            if (obj != null) result.Add(obj);
        }

        return result;
    }
}
