using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

/// <summary>Extension methods for registering <see cref="MicrosoftTeamsDataProvider"/> with dependency injection.</summary>
public static class MicrosoftTeamsDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="MicrosoftTeamsDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tenantId">Azure AD tenant ID.</param>
    /// <param name="clientId">Azure AD application (client) ID.</param>
    /// <param name="clientSecret">Azure AD client secret.</param>
    /// <param name="configure">Optional callback to further configure <see cref="MicrosoftTeamsOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddMicrosoftTeamsDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        Action<MicrosoftTeamsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var opts = new MicrosoftTeamsOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("MicrosoftTeams");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("MicrosoftTeams");
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph      = new GraphServiceClient(httpClient, credential);
            return new MicrosoftTeamsDataProvider(graph, opts);
        });
    }
}
