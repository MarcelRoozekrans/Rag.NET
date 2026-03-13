using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Grpc.Authentication;
using Rag.NET.Api.Grpc.Services;

namespace Rag.NET.Api.Grpc.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetGrpcApi(
        this IServiceCollection services,
        Action<RagGrpcApiOptions>? configure = null)
    {
        var options = new RagGrpcApiOptions();
        configure?.Invoke(options);

        services.Configure<GrpcApiKeyOptions>(o => o.ApiKeys = options.ApiKeys);
        services.AddSingleton<ApiKeyInterceptor>();
        services.AddGrpc(o => o.Interceptors.Add<ApiKeyInterceptor>());

        return services;
    }

    public static IEndpointRouteBuilder MapRagNetGrpcApi(this IEndpointRouteBuilder app)
    {
        app.MapGrpcService<RagGrpcService>();
        return app;
    }
}
