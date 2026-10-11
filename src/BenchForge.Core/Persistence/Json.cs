using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenchForge.Core.Persistence;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(false);
    public static readonly JsonSerializerOptions Indented = Create(true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? Indented : Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static byte[] SerializeGzip<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, value, Options);
        return ms.ToArray();
    }

    /// <summary>Reads JSON that may or may not be gzip-compressed.</summary>
    public static T? DeserializeMaybeGzip<T>(Stream s)
    {
        var buffered = new BufferedStream(s);
        Span<byte> head = stackalloc byte[2];
        int n = buffered.Read(head);
        var ms = new MemoryStream();
        ms.Write(head[..n]);
        buffered.CopyTo(ms);
        ms.Position = 0;
        if (n == 2 && head[0] == 0x1f && head[1] == 0x8b)
        {
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(gz, Options);
        }
        return JsonSerializer.Deserialize<T>(ms, Options);
    }
}
