using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Api.Authentication;

namespace Rag.NET.Api.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetApi(
        this IServiceCollection services,
        Action<RagApiOptions>? configure = null)
    {
        var options = new RagApiOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.Configure<ApiKeyOptions>(o => o.ApiKeys = options.ApiKeys);

        return services;
    }
}
