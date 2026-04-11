using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Rag.NET.DataProviders;

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

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));

        services.AddIConfluenceApi(options =>
            {
                options.BaseAddress = new Uri(baseUrl);
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new ConfluenceDataProvider(sp.GetRequiredService<IConfluenceApi>(), opts));
    }
}
