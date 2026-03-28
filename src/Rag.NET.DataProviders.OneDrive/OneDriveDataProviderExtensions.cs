using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.OneDrive;

/// <summary>DI registration extensions for <see cref="OneDriveDataProvider"/>.</summary>
public static class OneDriveDataProviderExtensions
{
    public static IServiceCollection AddOneDriveDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        string userId,
        Action<OneDriveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        services.AddDataProviderHttpClient("OneDrive");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var graph = new GraphServiceClient(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("OneDrive"),
            credential);

        var opts = new OneDriveOptions { UserId = userId };
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(new OneDriveDataProvider(graph, opts));
    }
}
