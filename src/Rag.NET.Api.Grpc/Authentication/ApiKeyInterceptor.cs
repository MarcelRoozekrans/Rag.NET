using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace Rag.NET.Api.Grpc.Authentication;

internal sealed class ApiKeyInterceptor(IOptions<GrpcApiKeyOptions> options) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ValidateKey(context);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateKey(context);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails closed: no configured keys is only acceptable when
    /// <see cref="GrpcApiKeyOptions.AllowAnonymous"/> was set explicitly.
    /// <c>AddRagNetGrpcApi</c> already rejects the no-keys/no-opt-out state at registration,
    /// but the options object is a mutable singleton, so this call-time check is the backstop
    /// for later mutation. When keys exist they win unconditionally — a mutated
    /// keys-plus-<c>AllowAnonymous</c> contradiction resolves to the stricter reading.
    /// </summary>
    private void ValidateKey(ServerCallContext context)
    {
        var current = options.Value;
        if (current.ApiKeys.Length == 0)
        {
            if (current.AllowAnonymous) return;
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "API-key authentication is not configured and anonymous access is not enabled."));
        }

        var key = context.RequestHeaders.GetValue("x-api-key");
        if (!current.ApiKeys.Contains(key, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing API key."));
    }
}
