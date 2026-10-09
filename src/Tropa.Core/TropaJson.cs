using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tropa.Core;

/// <summary>Общие настройки JSON для каталогов и профиля: правятся руками, поэтому комментарии и висячие запятые разрешены.</summary>
internal static class TropaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Для канала службы: одно сообщение = одна строка, без отступов.</summary>
    public static readonly JsonSerializerOptions Line = new(Options) { WriteIndented = false };

    public static T Load<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"пустой файл: {path}");
}
