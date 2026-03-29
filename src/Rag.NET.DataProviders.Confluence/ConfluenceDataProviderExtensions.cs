using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Confluence;

/// <summary>Extension methods for registering <see cref="ConfluenceDataProvider"/> with dependency injection.</summary>
public static class ConfluenceDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="ConfluenceDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUrl">Base URL of the Confluence instance.</param>
    /// <param name="email">Email address for Basic authentication.</param>
    /// <param name="apiToken">Atlassian API token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="ConfluenceOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddConfluenceDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string email,
        string apiToken,
        Action<ConfluenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ConfluenceOptions { BaseUrl = baseUrl, Email = email };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Confluence");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Confluence");
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            var api = RestService.For<IConfluenceApi>(http);
            return new ConfluenceDataProvider(api, opts);
        });
    }
}
