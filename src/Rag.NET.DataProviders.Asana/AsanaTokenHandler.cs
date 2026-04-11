using System.Net.Http.Headers;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

/// <summary>
/// Injects a dynamic Bearer token into every outgoing Asana API request.
/// The token is resolved per-request via the injected <see cref="ITokenProvider"/>,
/// which allows expiring tokens to be refreshed transparently.
/// </summary>
internal sealed class AsanaTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    public AsanaTokenHandler(ITokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
