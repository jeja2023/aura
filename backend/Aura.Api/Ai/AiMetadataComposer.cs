using System.Text.Json;
using Aura.Api.Serialization;

namespace Aura.Api.Ai;

internal static class AiMetadataComposer
{
    public static string Compose(
        string metadataJson,
        AiExtractResult aiResult,
        string? vectorId = null,
        AiUpsertResult? vectorUpsertResult = null,
        bool retryQueued = false,
        string? retryReason = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            var map = new Dictionary<string, object?>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    map[p.Name] = ReadJsonValue(p.Value);
                }
            }

            map["ai_success"] = aiResult.Success;
            map["ai_dim"] = aiResult.Dim;
            map["ai_msg"] = aiResult.Message;
            map["ai_status"] = ResolveAiStatus(aiResult, vectorUpsertResult, retryQueued);
            if (!string.IsNullOrWhiteSpace(vectorId))
            {
                map["ai_vector_id"] = vectorId;
            }

            if (vectorUpsertResult is not null)
            {
                map["ai_vector_success"] = vectorUpsertResult.Success;
                map["ai_vector_msg"] = vectorUpsertResult.Message;
                if (!string.IsNullOrWhiteSpace(vectorUpsertResult.Engine))
                {
                    map["ai_vector_engine"] = vectorUpsertResult.Engine;
                }
            }

            map["ai_retry_queued"] = retryQueued;
            if (!string.IsNullOrWhiteSpace(retryReason))
            {
                map["ai_retry_reason"] = retryReason;
            }

            return JsonSerializer.Serialize(map, AuraJsonSerializerOptions.Default);
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                raw = metadataJson,
                ai_success = aiResult.Success,
                ai_dim = aiResult.Dim,
                ai_msg = aiResult.Message,
                ai_status = ResolveAiStatus(aiResult, vectorUpsertResult, retryQueued),
                ai_vector_id = vectorId,
                ai_vector_success = vectorUpsertResult?.Success,
                ai_vector_msg = vectorUpsertResult?.Message,
                ai_vector_engine = vectorUpsertResult?.Engine,
                ai_retry_queued = retryQueued,
                ai_retry_reason = retryReason
            }, AuraJsonSerializerOptions.Default);
        }
    }

    private static object? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var int64) ? int64 : value.TryGetDouble(out var dbl) ? dbl : value.ToString(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static string ResolveAiStatus(AiExtractResult aiResult, AiUpsertResult? vectorUpsertResult, bool retryQueued)
    {
        if (!aiResult.Success)
        {
            return retryQueued ? "extract_retry_pending" : "extract_failed";
        }

        if (vectorUpsertResult is null)
        {
            return "extract_only";
        }

        if (vectorUpsertResult.Success)
        {
            return "ready";
        }

        return retryQueued ? "vector_retry_pending" : "vector_failed";
    }
}
