using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

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
            var token = tokenProvider.GetTokenAsync().AsTask().GetAwaiter().GetResult();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            return new AsanaDataProvider(RestService.For<IAsanaApi>(http), opts);
        });
    }
}
