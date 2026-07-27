using Azure.Core;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// A credential that is never redeemed — the token overload only needs a non-null one, and no
/// test in this suite opens a connection.
/// </summary>
internal sealed class FakeCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
