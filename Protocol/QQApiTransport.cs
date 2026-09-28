using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Centralizes QQBot authorization, token renewal and HTTP retry policy.</summary>
internal sealed class QQApiTransport(HttpClient http, QQPlatformConfig config, QQTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        for (var rateRetry = 0; rateRetry <= 3; rateRetry++)
        {
            var token = await tokens.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var response = await SendOnceAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                tokens.Invalidate();
                token = await tokens.GetAsync(refresh: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                response = await SendOnceAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests && rateRetry < 3)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                response.Dispose();
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            if (error.Code is > 0)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new QQApiException(path, status, error.Code.Value, error.Message, error.TraceId);
            }
            if (response.IsSuccessStatusCode) return response;
            var failedStatus = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"QQ OpenAPI {path} failed: HTTP {(int)failedStatus}; {error.Message ?? "unknown error"}",
                null, failedStatus);
        }
        throw new InvalidOperationException("Unreachable QQ retry state.");
    }

    private static async Task<(int? Code, string? Message, string? TraceId)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null) return (null, null, HeaderTraceId(response));
        var original = response.Content;
        var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var replay = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
            replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replay;
        original.Dispose();
        if (bytes.Length == 0) return (null, null, HeaderTraceId(response));
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, HeaderTraceId(response));
            int? code = null;
            if (root.TryGetProperty("err_code", out var rawCode))
            {
                if (rawCode.ValueKind == JsonValueKind.Number && rawCode.TryGetInt32(out var number)) code = number;
                else if (rawCode.ValueKind == JsonValueKind.String && int.TryParse(rawCode.GetString(), out var parsed)) code = parsed;
            }
            var message = root.TryGetProperty("message", out var rawMessage) && rawMessage.ValueKind == JsonValueKind.String
                ? rawMessage.GetString() : null;
            var trace = root.TryGetProperty("trace_id", out var rawTrace) && rawTrace.ValueKind == JsonValueKind.String
                ? rawTrace.GetString() : HeaderTraceId(response);
            return (code, message, trace);
        }
        catch (JsonException)
        {
            return (null, System.Text.Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 300)]), HeaderTraceId(response));
        }
    }

    private static string? HeaderTraceId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Tps-trace-ID", out var values) ? values.FirstOrDefault() : null;

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(config.ApiBaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class QQApiException(string path, HttpStatusCode status, int errorCode, string? detail, string? traceId)
    : HttpRequestException(
        $"QQ OpenAPI {path} failed: HTTP {(int)status}, err_code {errorCode}; {detail ?? "no details"}; trace_id {traceId ?? "unknown"}",
        null, status)
{
    public int ErrorCode { get; } = errorCode;
    public string? TraceId { get; } = traceId;
}
