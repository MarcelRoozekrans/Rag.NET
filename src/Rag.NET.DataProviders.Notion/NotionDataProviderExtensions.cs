using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

/// <summary>Extension methods for registering <see cref="NotionDataProvider"/> with dependency injection.</summary>
public static class NotionDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="NotionDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="integrationToken">Notion internal integration token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="NotionOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNotionDataProvider(
        this IServiceCollection services,
        string integrationToken,
        Action<NotionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationToken);

        var opts = new NotionOptions();
        configure?.Invoke(opts);

        services.AddINotionApi(options =>
            {
                options.BaseAddress = new Uri("https://api.notion.com");
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", integrationToken);
                // ZeroAlloc.Rest 0.2.0 [Header] only supports method/parameter targets, not interface level.
                // Keep Accept and Notion-Version here until the library adds class-level header support.
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
            })
            .AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new NotionDataProvider(sp.GetRequiredService<INotionApi>(), opts));
    }
}
