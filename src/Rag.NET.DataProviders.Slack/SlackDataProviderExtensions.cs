using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Slack;

public static class SlackDataProviderExtensions
{
    public static IServiceCollection AddSlackDataProvider(
        this IServiceCollection services,
        string botToken,
        Action<SlackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        var opts = new SlackOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Slack");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Slack");
            http.BaseAddress = new Uri("https://slack.com");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", botToken);
            return new SlackDataProvider(RestService.For<ISlackApi>(http), opts);
        });
    }
}
