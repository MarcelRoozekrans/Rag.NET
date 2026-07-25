using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Exchange;

/// <summary>Extension methods for registering <see cref="ExchangeMailDataProvider"/> with dependency injection.</summary>
public static class ExchangeMailDataProviderExtensions
{
    /// <summary>
    /// Registers an <see cref="ExchangeMailDataProvider"/> as an <see cref="IFileContentProvider"/> singleton
    /// using app-only authentication (client credentials flow). The app registration requires the
    /// <c>Mail.Read</c> application permission with admin consent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tenantId">Azure AD tenant ID.</param>
    /// <param name="clientId">Azure AD application (client) ID.</param>
    /// <param name="clientSecret">Azure AD client secret.</param>
    /// <param name="configure">
    /// Callback to configure <see cref="ExchangeMailOptions"/>; must set
    /// <see cref="ExchangeMailOptions.Mailbox"/> to a non-empty mailbox UPN.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddExchangeMailDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        Action<ExchangeMailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var opts = new ExchangeMailOptions();
        configure?.Invoke(opts);

        if (string.IsNullOrWhiteSpace(opts.Mailbox))
        {
            throw new ArgumentException(
                "ExchangeMailOptions.Mailbox must be set to a non-empty mailbox UPN.",
                nameof(configure));
        }

        services.AddDataProviderHttpClient("Exchange");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("Exchange");
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph      = new GraphServiceClient(httpClient, credential);
            return new ExchangeMailDataProvider(graph, opts);
        });
    }
}
