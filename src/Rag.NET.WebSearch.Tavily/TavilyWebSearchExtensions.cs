using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.WebSearch.Tavily;

/// <summary>Extension methods for registering Tavily web search with dependency injection.</summary>
public static class TavilyWebSearchExtensions
{
    /// <summary>
    /// Registers <see cref="IWebSearch"/> using Tavily as the backing provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apiKey">Tavily API key.</param>
    /// <param name="baseUrl">Override base URL (defaults to <c>https://api.tavily.com</c>). Used in tests.</param>
    public static IServiceCollection AddTavilyWebSearch(
        this IServiceCollection services,
        string apiKey,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var resolvedBaseUrl = string.IsNullOrEmpty(baseUrl) ? "https://api.tavily.com" : baseUrl;

        services.AddITavilyApi(options =>
            {
                options.BaseAddress = new Uri(resolvedBaseUrl);
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            // The credential goes here and nowhere else. Until #625 it was sent as an "api_key"
            // body field, which Tavily deprecated: their documented method and their own Python
            // SDK both use this header, and dev-tier keys reject the body form outright. Nothing
            // caught it because the cassette was hand-written and matched on path and method
            // alone, so no test in this repository could observe a credential at all.
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<IWebSearch>(sp =>
            new TavilyWebSearch(sp.GetRequiredService<ITavilyApi>()));

        return services;
    }
}
