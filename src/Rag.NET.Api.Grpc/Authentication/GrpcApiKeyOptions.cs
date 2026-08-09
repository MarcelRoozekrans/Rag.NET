namespace Rag.NET.Api.Grpc.Authentication;

internal sealed class GrpcApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>
    /// Explicit opt-out of API-key authentication. <see langword="false"/> by default: with no
    /// keys configured and no opt-out, registration throws and the interceptor fails closed
    /// (<c>Unauthenticated</c>) rather than silently serving every call. When keys exist they
    /// win — this flag only ever grants access in the complete absence of keys.
    /// </summary>
    public bool AllowAnonymous { get; set; }
}
