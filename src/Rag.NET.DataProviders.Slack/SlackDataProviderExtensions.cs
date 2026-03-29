using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Slack;

/// <summary>Extension methods for registering <see cref="SlackDataProvider"/> with dependency injection.</summary>
public static class SlackDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="SlackDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="botToken">Slack bot OAuth token (e.g. <c>xoxb-...</c>).</param>
    /// <param name="configure">Optional callback to further configure <see cref="SlackOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
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
