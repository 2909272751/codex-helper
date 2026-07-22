using System.Text.Json;

namespace CodexHelper.Core.Infrastructure;

public sealed class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public T LoadOrCreate<T>(string path, Func<T> factory) where T : class
    {
        if (!File.Exists(path))
        {
            return factory();
        }

        var content = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(content, Options)
            ?? throw new InvalidDataException($"无法读取 JSON：{Path.GetFileName(path)}");
    }

    public void Save<T>(string path, T value)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> value) =>
        JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("JSON 内容为空或格式无效。");
}
