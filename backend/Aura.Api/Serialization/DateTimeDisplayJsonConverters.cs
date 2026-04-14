/* 文件：时间 JSON 转换器 | File: Date/time JSON converters */
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aura.Api.Serialization;

/// <summary>
/// 将 DateTime 序列化为 <c>yyyy-MM-dd HH:mm:ss</c>，反序列化兼容 ISO 与上述格式。
/// </summary>
internal sealed class DateTimeDisplayJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return default;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            if (DateTime.TryParse(s, out dt)) return dt;
            return default;
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        writer.WriteStringValue(local.ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// 将 DateTimeOffset 序列化为 <c>yyyy-MM-dd HH:mm:ss</c>（按偏移换算后的日历时间），反序列化兼容 ISO。
/// </summary>
internal sealed class DateTimeOffsetDisplayJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s)) return default;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                return dto;
            if (DateTimeOffset.TryParse(s, out dto)) return dto;
            return default;
        }
        return reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
