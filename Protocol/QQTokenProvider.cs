using System.Net.Http.Json;
using ShiroBot.Adapter.QQPlatform.Wire;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQTokenProvider(HttpClient http, QQPlatformConfig config)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _token is not null && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(90))
            return _token;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _token is not null && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(90))
                return _token;
            using var response = await http.PostAsJsonAsync(
                config.TokenEndpoint, new TokenRequest(config.AppId, config.AppSecret), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"QQ token request failed: HTTP {(int)response.StatusCode}");
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("QQ token response is empty.");
            if (payload.Code is > 0)
            {
                if (payload.Code is 100007 or 100016 or 10004)
                    throw new QQAuthenticationException(payload.Code, payload.Message);
                throw new QQTokenException(payload.Code, payload.Message);
            }
            if (string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new QQTokenException(payload.Code, payload.Message);
            _token = payload.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, payload.ExpiresIn));
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _expiresAt = DateTimeOffset.MinValue;
}

internal class QQTokenException(int? code, string? detail)
    : Exception($"QQ token request failed: code {code?.ToString() ?? "unknown"}; {detail ?? "no access token"}.");

internal sealed class QQAuthenticationException(int? code, string? detail)
    : QQTokenException(code, detail);
