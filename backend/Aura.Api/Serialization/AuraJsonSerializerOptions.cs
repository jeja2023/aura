/* 文件：JSON 序列化选项 | File: JSON serializer options */
using System.Text.Json;

namespace Aura.Api.Serialization;

/// <summary>
/// 全站 API 与 Redis 缓存共用的 JSON 选项：时间字段不以 ISO「T」形式输出。
/// </summary>
internal static class AuraJsonSerializerOptions
{
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        o.Converters.Add(new DateTimeDisplayJsonConverter());
        o.Converters.Add(new DateTimeOffsetDisplayJsonConverter());
        return o;
    }
}
