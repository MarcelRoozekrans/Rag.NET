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

    private void ValidateKey(ServerCallContext context)
    {
        if (options.Value.ApiKeys.Length == 0) return;
        var key = context.RequestHeaders.GetValue("x-api-key");
        if (!options.Value.ApiKeys.Contains(key, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing API key."));
    }
}
