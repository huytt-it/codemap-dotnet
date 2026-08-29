using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeMap.Query.Json;

internal static class JsonUtil
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static void WriteIndented<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, Indented));

    public static T? ReadFile<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Indented);
}
