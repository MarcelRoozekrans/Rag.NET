using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Confluence;

public static class ConfluenceDataProviderExtensions
{
    public static IServiceCollection AddConfluenceDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string email,
        string apiToken,
        Action<ConfluenceOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var opts = new ConfluenceOptions { BaseUrl = baseUrl, Email = email };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Confluence");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Confluence");
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            var api = RestService.For<IConfluenceApi>(http);
            return new ConfluenceDataProvider(api, opts);
        });
    }
}
