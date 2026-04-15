using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

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

        services.AddISlackApi(options =>
            {
                options.BaseAddress = new Uri("https://slack.com");
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", botToken);
                // ZeroAlloc.Rest 0.2.0 [Header] only supports method/parameter targets, not interface level.
                // Keep Accept here until the library adds class-level header support.
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new SlackDataProvider(sp.GetRequiredService<ISlackApi>(), opts));
    }
}
