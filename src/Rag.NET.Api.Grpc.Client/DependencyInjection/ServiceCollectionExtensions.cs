using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Api.Grpc.Proto;

namespace Rag.NET.Api.Grpc.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GrpcRagPipeline"/> as <see cref="IRagPipeline"/> using a typed gRPC client.
    /// </summary>
    /// <remarks>
    /// For HTTP (non-TLS) connections in tests or local development, set:
    /// <code>AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);</code>
    /// </remarks>
    public static IServiceCollection AddRagNetGrpcClient(
        this IServiceCollection services,
        Action<RagGrpcClientOptions> configure)
    {
        var options = new RagGrpcClientOptions { BaseUrl = string.Empty, ApiKey = string.Empty };
        configure(options);

        services.AddGrpcClient<RagService.RagServiceClient>(o => o.Address = new Uri(options.BaseUrl))
            .AddCallCredentials((context, metadata) =>
            {
                metadata.Add("x-api-key", options.ApiKey);
                return Task.CompletedTask;
            });

        services.AddTransient<IRagPipeline, GrpcRagPipeline>();

        return services;
    }
}
