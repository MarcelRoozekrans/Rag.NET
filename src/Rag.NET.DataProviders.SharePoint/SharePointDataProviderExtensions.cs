using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.SharePoint;

/// <summary>DI registration extensions for <see cref="SharePointDataProvider"/>.</summary>
public static class SharePointDataProviderExtensions
{
    public static IServiceCollection AddSharePointDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        string siteId,
        string driveId,
        Action<SharePointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);

        services.AddDataProviderHttpClient("SharePoint");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graph = new GraphServiceClient(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("SharePoint"),
            credential);

        var opts = new SharePointOptions { SiteId = siteId, DriveId = driveId };
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(new SharePointDataProvider(graph, opts));
    }
}
