using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

public static class MicrosoftTeamsDataProviderExtensions
{
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
