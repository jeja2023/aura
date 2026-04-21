/* 文件：统一 API 错误响应模型 | File: Unified API error response model */
using System.Text.Json.Serialization;

namespace Aura.Api.Models;

/// <summary>
/// 与成功响应中 code/msg 约定一致，供业务错误与 5xx 显式返回使用；避免混用 Problem Details 导致前端解析不稳定。
/// </summary>
internal sealed record ApiErrorResponse(
    int Code,
    string Msg,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Data = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TraceId = null);
