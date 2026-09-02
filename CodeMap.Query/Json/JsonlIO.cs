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

        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // One malformed line (a half-written file from a killed process, a hand-edited fixture) should not
            // take down the whole command — it's one row out of possibly millions. Only JSON syntax errors are
            // swallowed here; anything else (e.g. an I/O failure reading the file itself) still propagates, and
            // CliApp's catch-all turns that into a short message instead of a raw stack trace.
            try
            {
                var obj = JsonSerializer.Deserialize<T>(line, JsonUtil.Compact);
                if (obj != null) result.Add(obj);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Warning: {path}:{lineNumber} is not valid JSON and was skipped ({ex.Message})");
            }
        }

        return result;
    }
}
