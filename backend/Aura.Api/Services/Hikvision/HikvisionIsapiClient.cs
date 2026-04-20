/* 文件：海康 ISAPI HTTP 客户端（HikvisionIsapiClient.cs） | File: Hikvision ISAPI HTTP client */
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Aura.Api.Services.Hikvision;

/// <summary>
/// 使用 Digest 认证访问 NVR ISAPI，行为对齐官方 CommonBase.HttpClient（CredentialCache + Digest）。
/// </summary>
internal sealed class HikvisionIsapiClient
{
    private readonly ILogger<HikvisionIsapiClient> _logger;
    private readonly IOptions<HikvisionIsapiOptions> _options;

    public HikvisionIsapiClient(ILogger<HikvisionIsapiClient> logger, IOptions<HikvisionIsapiOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<IsapiTransportResult<string>> GetStringAsync(
        Uri baseUri,
        string pathAndQuery,
        string userName,
        string password,
        TimeSpan timeout,
        bool skipSslValidation,
        CancellationToken cancellationToken)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var opt = _options.Value;
        var sw = Stopwatch.StartNew();
        using var activity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartOutbound("isapi.get_string", baseUri, pathAndQuery)
            : null;
        _ = activity;

        async Task<IsapiTransportResult<string>> SendCoreAsync()
        {
            var requestUri = new Uri(baseUri, pathAndQuery);
            using var handler = CreateHandler(baseUri, userName, password, skipSslValidation);
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };

            try
            {
                using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return IsapiTransportResult<string>.Ok((int)response.StatusCode, text);
                }

                _logger.LogWarning("海康 ISAPI 请求返回非成功状态。状态码={StatusCode}, 路径={Path}", (int)response.StatusCode, pathAndQuery);
                return IsapiTransportResult<string>.FailWithBody((int)response.StatusCode, text, "设备返回错误状态");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IsapiTransportResult<string>.FailNoBody(null, "请求超时");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "海康 ISAPI 请求异常。路径={Path}", pathAndQuery);
                return IsapiTransportResult<string>.FailNoBody(null, ex.Message);
            }
        }

        var result = await SendCoreAsync();
        HikvisionIsapiMetrics.ObserveOutbound("get_string", result.Success, sw.Elapsed.TotalSeconds);
        return result;
    }

    public async Task<IsapiTransportResult<byte[]>> GetBytesAsync(
        Uri baseUri,
        string pathAndQuery,
        string userName,
        string password,
        TimeSpan timeout,
        bool skipSslValidation,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var opt = _options.Value;
        var sw = Stopwatch.StartNew();
        using var activity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartOutbound("isapi.get_bytes", baseUri, pathAndQuery)
            : null;
        _ = activity;

        async Task<IsapiTransportResult<byte[]>> SendCoreAsync()
        {
            var requestUri = new Uri(baseUri, pathAndQuery);
            using var handler = CreateHandler(baseUri, userName, password, skipSslValidation);
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };

            try
            {
                using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var errText = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("海康 ISAPI 二进制请求失败。状态码={StatusCode}, 路径={Path}", (int)response.StatusCode, pathAndQuery);
                    return IsapiTransportResult<byte[]>.FailWithBody((int)response.StatusCode, errText, errText);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > maxBytes)
                    {
                        return IsapiTransportResult<byte[]>.FailNoBody((int)response.StatusCode, "抓图数据超过配置的最大字节数");
                    }
                }

                return IsapiTransportResult<byte[]>.Ok((int)response.StatusCode, ms.ToArray());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IsapiTransportResult<byte[]>.FailNoBody(null, "请求超时");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "海康 ISAPI 二进制请求异常。路径={Path}", pathAndQuery);
                return IsapiTransportResult<byte[]>.FailNoBody(null, ex.Message);
            }
        }

        var result = await SendCoreAsync();
        HikvisionIsapiMetrics.ObserveOutbound("get_bytes", result.Success, sw.Elapsed.TotalSeconds);
        return result;
    }

    private static SocketsHttpHandler CreateHandler(Uri baseUri, string userName, string password, bool skipSslValidation)
    {
        var cache = new CredentialCache();
        cache.Add(baseUri, "Digest", new NetworkCredential(userName, password));

        var handler = new SocketsHttpHandler
        {
            Credentials = cache,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        if (skipSslValidation && string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (
                _,
                _,
                _,
                _) => true;
        }

        return handler;
    }

    /// <summary>
    /// 与官方 <c>CommonMethod.UploadPic</c> 一致：<c>multipart/form-data</c> 字段名 <c>imageFile</c>，POST <c>/ISAPI/SDT/pictureUpload</c>。
    /// </summary>
    public async Task<IsapiTransportResult<HikvisionIsapiHttpPayload>> SendMultipartPostAsync(
        Uri baseUri,
        string pathAndQuery,
        string formFieldName,
        string fileName,
        string partContentType,
        byte[] fileBytes,
        string userName,
        string password,
        TimeSpan timeout,
        bool skipSslValidation,
        int maxTextChars,
        CancellationToken cancellationToken)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var opt = _options.Value;
        var sw = Stopwatch.StartNew();
        using var activity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartOutbound("isapi.multipart_post", baseUri, pathAndQuery)
            : null;
        _ = activity;

        async Task<IsapiTransportResult<HikvisionIsapiHttpPayload>> SendCoreAsync()
        {
            var requestUri = new Uri(baseUri, pathAndQuery);
            using var handler = CreateHandler(baseUri, userName, password, skipSslValidation);
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };

            using var multipart = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(partContentType);
            multipart.Add(fileContent, formFieldName, fileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = multipart
            };

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var mediaType = response.Content.Headers.ContentType?.MediaType;

                if (!response.IsSuccessStatusCode)
                {
                    var errText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailWithBody((int)response.StatusCode, errText, "设备返回错误状态");
                }

                await using var textStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var sr = new StreamReader(textStream, Encoding.UTF8, leaveOpen: false);
                var text = await sr.ReadToEndAsync(cancellationToken);
                if (text.Length > maxTextChars)
                {
                    return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody((int)response.StatusCode, "响应文本超过上限");
                }

                var textPayload = new HikvisionIsapiHttpPayload
                {
                    IsBinary = false,
                    TextBody = text,
                    ContentType = mediaType
                };
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.Ok((int)response.StatusCode, textPayload);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody(null, "请求超时");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "海康 ISAPI 多部分上传异常。路径={Path}", pathAndQuery);
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody(null, ex.Message);
            }
        }

        var result = await SendCoreAsync();
        HikvisionIsapiMetrics.ObserveOutbound("multipart_post", result.Success, sw.Elapsed.TotalSeconds);
        return result;
    }

    /// <summary>通用请求，对齐 Demo 中 GET/PUT/POST/DELETE 与文本或图片响应。</summary>
    public async Task<IsapiTransportResult<HikvisionIsapiHttpPayload>> SendAsync(
        HttpMethod method,
        Uri baseUri,
        string pathAndQuery,
        byte[]? requestBody,
        string? requestMediaType,
        string userName,
        string password,
        TimeSpan timeout,
        bool skipSslValidation,
        bool preferBinaryResponse,
        int maxBinaryBytes,
        int maxTextChars,
        CancellationToken cancellationToken)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var opt = _options.Value;
        var sw = Stopwatch.StartNew();
        using var activity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartOutbound("isapi.send", baseUri, pathAndQuery)
            : null;
        _ = activity;

        async Task<IsapiTransportResult<HikvisionIsapiHttpPayload>> SendCoreAsync()
        {
            var requestUri = new Uri(baseUri, pathAndQuery);
            using var handler = CreateHandler(baseUri, userName, password, skipSslValidation);
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };

            using var request = new HttpRequestMessage(method, requestUri);
            if (requestBody is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(requestBody);
                if (!string.IsNullOrWhiteSpace(requestMediaType))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(requestMediaType);
                }
            }

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                var binary =
                    preferBinaryResponse
                    || (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                    || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

                if (!response.IsSuccessStatusCode)
                {
                    var errText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailWithBody((int)response.StatusCode, errText, "设备返回错误状态");
                }

                if (binary)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var ms = new MemoryStream();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                        if (ms.Length > maxBinaryBytes)
                        {
                            return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody((int)response.StatusCode, "响应二进制数据超过上限");
                        }
                    }

                    var payload = new HikvisionIsapiHttpPayload
                    {
                        IsBinary = true,
                        BinaryBody = ms.ToArray(),
                        ContentType = mediaType
                    };
                    return IsapiTransportResult<HikvisionIsapiHttpPayload>.Ok((int)response.StatusCode, payload);
                }

                await using var textStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var sr = new StreamReader(textStream, Encoding.UTF8, leaveOpen: false);
                var text = await sr.ReadToEndAsync(cancellationToken);
                if (text.Length > maxTextChars)
                {
                    return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody((int)response.StatusCode, "响应文本超过上限");
                }

                var textPayload = new HikvisionIsapiHttpPayload
                {
                    IsBinary = false,
                    TextBody = text,
                    ContentType = mediaType
                };
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.Ok((int)response.StatusCode, textPayload);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody(null, "请求超时");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "海康 ISAPI 通用请求异常。方法={Method}, 路径={Path}", method, pathAndQuery);
                return IsapiTransportResult<HikvisionIsapiHttpPayload>.FailNoBody(null, ex.Message);
            }
        }

        var result = await SendCoreAsync();
        HikvisionIsapiMetrics.ObserveOutbound("send", result.Success, sw.Elapsed.TotalSeconds);
        return result;
    }

    /// <summary>
    /// 订阅设备 <c>alertStream</c>（长连接）。使用独立 <see cref="SocketsHttpHandler"/>，避免连接池生命周期截断长读。
    /// </summary>
    public async Task RunAlertStreamAsync(
        Uri baseUri,
        string pathAndQuery,
        string userName,
        string password,
        bool skipSslValidation,
        int maxBufferBytes,
        Func<string, byte[], Task> onPartAsync,
        ILogger logger,
        CancellationToken cancellationToken,
        Action? onStreamEstablished = null)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var opt = _options.Value;
        var sw = Stopwatch.StartNew();
        using var activity = opt.TelemetryActivitiesEnabled
            ? HikvisionIsapiActivity.StartOutbound("isapi.alert_stream", baseUri, pathAndQuery)
            : null;
        _ = activity;

        var requestUri = new Uri(baseUri, pathAndQuery);
        using var handler = CreateLongLivedHandler(baseUri, userName, password, skipSslValidation);
        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var streamCompleted = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "海康告警长连接返回非成功状态。状态码={Status}, 路径={Path}, 正文预览={Preview}",
                    (int)response.StatusCode,
                    pathAndQuery,
                    HikvisionIsapiLogFormatting.TruncateForLog(err, 256));
                return;
            }

            var contentTypeHeader = response.Content.Headers.ContentType?.ToString() ?? "";
            var boundary = ExtractMultipartBoundary(contentTypeHeader);
            if (string.IsNullOrEmpty(boundary))
            {
                logger.LogWarning("海康告警长连接响应缺少 multipart boundary。Content-Type={ContentType}", contentTypeHeader);
                return;
            }

            onStreamEstablished?.Invoke();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new List<byte>(65536);
            var chunk = new byte[65536];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    logger.LogInformation("海康告警长连接对端关闭。路径={Path}", pathAndQuery);
                    streamCompleted = true;
                    break;
                }

                buffer.AddRange(chunk.AsSpan(0, read));
                if (buffer.Count > maxBufferBytes)
                {
                    logger.LogWarning(
                        "海康告警流缓冲区超过上限已清空。上限={Max} 字节，路径={Path}",
                        maxBufferBytes,
                        pathAndQuery);
                    buffer.Clear();
                }

                await HikvisionAlertStreamMultipartParser.DrainBufferAsync(
                        buffer,
                        boundary,
                        onPartAsync,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested && !streamCompleted)
            {
                streamCompleted = true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("海康告警长连接在建立阶段超时。路径={Path}", pathAndQuery);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "海康告警长连接异常。路径={Path}", pathAndQuery);
        }
        finally
        {
            HikvisionIsapiMetrics.ObserveOutbound("alert_stream", streamCompleted, sw.Elapsed.TotalSeconds);
        }
    }

    private static SocketsHttpHandler CreateLongLivedHandler(Uri baseUri, string userName, string password, bool skipSslValidation)
    {
        var cache = new CredentialCache();
        cache.Add(baseUri, "Digest", new NetworkCredential(userName, password));

        var handler = new SocketsHttpHandler
        {
            Credentials = cache,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        if (skipSslValidation && string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (
                _,
                _,
                _,
                _) => true;
        }

        return handler;
    }

    private static string? ExtractMultipartBoundary(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var idx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var v = contentType[(idx + "boundary=".Length)..].Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            v = v[1..^1];
        }

        var semi = v.IndexOf(';');
        if (semi >= 0)
        {
            v = v[..semi].Trim();
        }

        return string.IsNullOrEmpty(v) ? null : v;
    }
}

internal readonly record struct IsapiTransportResult<T>(bool Success, int? HttpStatus, T? Data, string? ErrorBody, string? Message)
{
    public static IsapiTransportResult<T> Ok(int httpStatus, T data) =>
        new(true, httpStatus, data, null, null);

    public static IsapiTransportResult<T> FailWithBody(int httpStatus, string errorBody, string message) =>
        new(false, httpStatus, default, errorBody, message);

    public static IsapiTransportResult<T> FailNoBody(int? httpStatus, string message) =>
        new(false, httpStatus, default, null, message);

}
