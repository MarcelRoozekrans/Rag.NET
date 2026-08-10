using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Extension methods for registering Zendesk data providers with dependency injection.</summary>
public static class ZendeskDataProviderExtensions
{
    private sealed class ZendeskApiMarker { }

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
        Action<ZendeskTicketsOptions>? configure = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ZendeskTicketsOptions { Subdomain = subdomain, Email = email };
        configure?.Invoke(opts);

        // Credentials are derived from the options object after the configure callback ran, so
        // a callback that changed Email changes the credentials sent (issue #108 found the raw
        // parameter being used instead, silently ignoring the callback).
        ArgumentException.ThrowIfNullOrWhiteSpace(opts.Email, nameof(configure));

        var resolvedBaseUrl = string.IsNullOrEmpty(baseUrl) ? $"https://{opts.Subdomain}.zendesk.com" : baseUrl;
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{opts.Email}/token:{apiToken}"));

        if (!services.Any(d => d.ServiceType == typeof(ZendeskApiMarker)))
        {
            services.AddSingleton<ZendeskApiMarker>();
            services.AddIZendeskApi(options =>
                {
                    options.BaseAddress = new Uri(resolvedBaseUrl);
                    options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
                })
                .ConfigureHttpClient(client =>
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Basic", credentials);
                    // ZeroAlloc.Rest 0.2.0 [Header] only supports method/parameter targets, not interface level.
                    // Keep Accept here until the library adds class-level header support.
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler();
        }

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
        Action<ZendeskArticlesOptions>? configure = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ZendeskArticlesOptions { Subdomain = subdomain, Email = email };
        configure?.Invoke(opts);

        // Same single-source-of-truth rule as the tickets registration: behaviour reads the
        // options object, never the raw parameters, once the configure callback has run.
        ArgumentException.ThrowIfNullOrWhiteSpace(opts.Email, nameof(configure));

        var resolvedBaseUrl = string.IsNullOrEmpty(baseUrl) ? $"https://{opts.Subdomain}.zendesk.com" : baseUrl;
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{opts.Email}/token:{apiToken}"));

        if (!services.Any(d => d.ServiceType == typeof(ZendeskApiMarker)))
        {
            services.AddSingleton<ZendeskApiMarker>();
            services.AddIZendeskApi(options =>
                {
                    options.BaseAddress = new Uri(resolvedBaseUrl);
                    options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
                })
                .ConfigureHttpClient(client =>
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Basic", credentials);
                    // ZeroAlloc.Rest 0.2.0 [Header] only supports method/parameter targets, not interface level.
                    // Keep Accept here until the library adds class-level header support.
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                })
                .AddStandardResilienceHandler();
        }

        return services.AddSingleton<IFileContentProvider>(sp =>
            new ZendeskArticlesDataProvider(sp.GetRequiredService<IZendeskApi>(), opts));
    }
}
