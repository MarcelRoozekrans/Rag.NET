using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders;

/// <summary>
/// Fetches and auto-refreshes a bearer token using the OAuth 2.0 client credentials flow.
/// The token is refreshed proactively 60 seconds before expiry.
/// </summary>
public sealed partial class OAuthClientCredentialsTokenProvider : ITokenProvider, IDisposable
{
    private readonly string _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scopeParam;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public OAuthClientCredentialsTokenProvider(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string[]? scopes = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scopeParam = scopes is { Length: > 0 } ? string.Join(' ', scopes) : string.Empty;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        // Fast path — token still valid
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside lock
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _clientId,
                ["client_secret"] = _clientSecret,
            };
            if (!string.IsNullOrEmpty(_scopeParam))
                form["scope"] = _scopeParam;

            using var response = await _http.PostAsync(
                _tokenEndpoint,
                new FormUrlEncodedContent(form),
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(
                OAuthResponseContext.Default.OAuthTokenResponse,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OAuth token response was empty.");

            _cachedToken = result.AccessToken;
            // Refresh 60 seconds before expiry
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn - 60);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _lock.Dispose();
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")]   int ExpiresIn);

    [JsonSerializable(typeof(OAuthTokenResponse))]
    private sealed partial class OAuthResponseContext : JsonSerializerContext;
}
