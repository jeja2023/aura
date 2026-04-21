using Aura.Api.Models;
using Aura.Api.Serialization;
using Microsoft.AspNetCore.Http;

namespace Aura.Api.Internal;

internal static class AuraApiResults
{
    private const string JsonUtf8 = "application/json; charset=utf-8";

    private static IResult Error(int statusCode, string msg, int code, object? data = null, string? traceId = null) =>
        Results.Json(
            new ApiErrorResponse(code, msg, data, traceId),
            AuraJsonSerializerOptions.Default,
            JsonUtf8,
            statusCode);

    public static Task WriteErrorAsync(
        HttpResponse response,
        int statusCode,
        string msg,
        int code,
        object? data = null,
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        response.StatusCode = statusCode;
        response.ContentType = JsonUtf8;
        return response.WriteAsJsonAsync(
            new ApiErrorResponse(code, msg, data, traceId),
            AuraJsonSerializerOptions.Default,
            cancellationToken);
    }

    public static IResult BadRequest(string msg, int code = 40000, object? data = null) =>
        Error(StatusCodes.Status400BadRequest, msg, code, data);

    public static IResult Unauthorized(string msg = "未授权", int code = 40100, object? data = null) =>
        Error(StatusCodes.Status401Unauthorized, msg, code, data);

    public static IResult Forbidden(string msg, int code = 40300, object? data = null) =>
        Error(StatusCodes.Status403Forbidden, msg, code, data);

    public static IResult NotFound(string msg, int code = 40400, object? data = null) =>
        Error(StatusCodes.Status404NotFound, msg, code, data);

    public static IResult Conflict(string msg, int code = 40900, object? data = null) =>
        Error(StatusCodes.Status409Conflict, msg, code, data);

    public static IResult TooManyRequests(string msg, int code = 42900, object? data = null) =>
        Error(StatusCodes.Status429TooManyRequests, msg, code, data);

    public static IResult BadGateway(string msg, int code = 50200, object? data = null) =>
        Error(StatusCodes.Status502BadGateway, msg, code, data);

    public static IResult ServiceUnavailable(string msg, int code = 50300, object? data = null) =>
        Error(StatusCodes.Status503ServiceUnavailable, msg, code, data);

    public static IResult InternalServerError(string msg, int code = 50000, object? data = null) =>
        Error(StatusCodes.Status500InternalServerError, msg, code, data);
}
