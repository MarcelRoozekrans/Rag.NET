using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Api.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetApiClient(
        this IServiceCollection services,
        Action<RagApiClientOptions> configure)
    {
        var options = new RagApiClientOptions { BaseUrl = "", ApiKey = "" };
        configure(options);

        services.AddHttpClient<IRagPipeline, HttpRagPipeline>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        });

        return services;
    }
}
