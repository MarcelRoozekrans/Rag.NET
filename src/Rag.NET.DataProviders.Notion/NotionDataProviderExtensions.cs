using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Notion;

public static class NotionDataProviderExtensions
{
    public static IServiceCollection AddNotionDataProvider(
        this IServiceCollection services,
        string integrationToken,
        Action<NotionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationToken);

        var opts = new NotionOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Notion");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Notion");
            http.BaseAddress = new Uri("https://api.notion.com");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", integrationToken);
            return new NotionDataProvider(RestService.For<INotionApi>(http), opts);
        });
    }
}
