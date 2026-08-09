using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Grpc.Authentication;
using Rag.NET.Api.Grpc.Services;

namespace Rag.NET.Api.Grpc.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gRPC service. Authentication must be decided explicitly: configure at
    /// least one key in <see cref="RagGrpcApiOptions.ApiKeys"/>, or opt out with
    /// <see cref="RagGrpcApiOptions.AllowAnonymous"/> — neither (an accidentally open service)
    /// and both (a contradiction) fail here, so misconfiguration fails at registration time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="RagGrpcApiOptions"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="RagGrpcApiOptions.ApiKeys"/> is empty while
    /// <see cref="RagGrpcApiOptions.AllowAnonymous"/> is <see langword="false"/>, and when
    /// both are set at once.
    /// </exception>
    public static IServiceCollection AddRagNetGrpcApi(
        this IServiceCollection services,
        Action<RagGrpcApiOptions>? configure = null)
    {
        var options = new RagGrpcApiOptions();
        configure?.Invoke(options);

        if (options.ApiKeys.Length == 0 && !options.AllowAnonymous)
        {
            throw new InvalidOperationException(
                "AddRagNetGrpcApi: RagGrpcApiOptions.ApiKeys is empty and RagGrpcApiOptions.AllowAnonymous " +
                "is false — this would serve every call unauthenticated by accident. Either configure at " +
                "least one key (o => o.ApiKeys = [\"...\"]) or opt out of authentication explicitly " +
                "(o => o.AllowAnonymous = true).");
        }

        if (options.ApiKeys.Length > 0 && options.AllowAnonymous)
        {
            throw new InvalidOperationException(
                "AddRagNetGrpcApi: RagGrpcApiOptions.AllowAnonymous is true but RagGrpcApiOptions.ApiKeys " +
                "is non-empty — the two contradict each other. Remove AllowAnonymous to require a key, or " +
                "remove the keys to serve anonymously.");
        }

        services.Configure<GrpcApiKeyOptions>(o =>
        {
            o.ApiKeys = options.ApiKeys;
            o.AllowAnonymous = options.AllowAnonymous;
        });
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
