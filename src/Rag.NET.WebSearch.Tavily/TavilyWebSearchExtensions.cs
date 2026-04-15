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
            .AddStandardResilienceHandler();

        services.AddSingleton<IWebSearch>(sp =>
            new TavilyWebSearch(sp.GetRequiredService<ITavilyApi>(), apiKey));

        return services;
    }
}
