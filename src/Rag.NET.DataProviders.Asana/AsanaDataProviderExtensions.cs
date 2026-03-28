using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

public static class AsanaDataProviderExtensions
{
    public static IServiceCollection AddAsanaDataProvider(
        this IServiceCollection services,
        string personalAccessToken,
        string workspaceGid,
        Action<AsanaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);
        return services.AddAsanaDataProvider(
            new StaticTokenProvider(personalAccessToken), workspaceGid, configure);
    }

    public static IServiceCollection AddAsanaDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        string workspaceGid,
        Action<AsanaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceGid);

        var opts = new AsanaOptions { WorkspaceGid = workspaceGid };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Asana");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Asana");
            http.BaseAddress = new Uri("https://app.asana.com");
            // Token is resolved per-request inside AsanaDataProvider.GetHandlesAsync
            // to avoid sync-over-async in the factory and to handle token expiry correctly.
            return new AsanaDataProvider(http, tokenProvider, opts);
        });
    }
}
