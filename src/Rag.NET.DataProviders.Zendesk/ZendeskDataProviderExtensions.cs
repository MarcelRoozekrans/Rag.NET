using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Extension methods for registering Zendesk data providers with dependency injection.</summary>
public static class ZendeskDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="ZendeskTicketsDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="subdomain">Zendesk subdomain (e.g. <c>"mycompany"</c>).</param>
    /// <param name="email">Agent email for Basic authentication.</param>
    /// <param name="apiToken">Zendesk API token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="ZendeskTicketsOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddZendeskTicketsDataProvider(
        this IServiceCollection services,
        string subdomain,
        string email,
        string apiToken,
        Action<ZendeskTicketsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ZendeskTicketsOptions { Subdomain = subdomain, Email = email };
        configure?.Invoke(opts);

        var baseUrl = $"https://{subdomain}.zendesk.com";
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}/token:{apiToken}"));

        services.AddIZendeskApi(options =>
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
            new ZendeskTicketsDataProvider(sp.GetRequiredService<IZendeskApi>(), opts));
    }

    /// <summary>
    /// Registers a <see cref="ZendeskArticlesDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="subdomain">Zendesk subdomain (e.g. <c>"mycompany"</c>).</param>
    /// <param name="email">Agent email for Basic authentication.</param>
    /// <param name="apiToken">Zendesk API token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="ZendeskArticlesOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddZendeskArticlesDataProvider(
        this IServiceCollection services,
        string subdomain,
        string email,
        string apiToken,
        Action<ZendeskArticlesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ZendeskArticlesOptions { Subdomain = subdomain, Email = email };
        configure?.Invoke(opts);

        var baseUrl = $"https://{subdomain}.zendesk.com";
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}/token:{apiToken}"));

        services.AddIZendeskApi(options =>
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
            new ZendeskArticlesDataProvider(sp.GetRequiredService<IZendeskApi>(), opts));
    }
}
