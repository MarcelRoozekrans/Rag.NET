using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

/// <summary>Extension methods for registering <see cref="AsanaDataProvider"/> with dependency injection.</summary>
public static class AsanaDataProviderExtensions
{
    /// <summary>
    /// Registers an <see cref="AsanaDataProvider"/> as an <see cref="IFileContentProvider"/> singleton
    /// using a static personal access token.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="personalAccessToken">Asana personal access token.</param>
    /// <param name="workspaceGid">Asana workspace GID.</param>
    /// <param name="configure">Optional callback to further configure <see cref="AsanaOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
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

    /// <summary>
    /// Registers an <see cref="AsanaDataProvider"/> as an <see cref="IFileContentProvider"/> singleton
    /// using a custom <see cref="ITokenProvider"/> for token refresh.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tokenProvider">Provider that supplies (and optionally refreshes) the bearer token.</param>
    /// <param name="workspaceGid">Asana workspace GID.</param>
    /// <param name="configure">Optional callback to further configure <see cref="AsanaOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
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

        // Register the token handler as transient so DI can inject the caller-supplied
        // ITokenProvider into it when the HttpClient pipeline is constructed.
        services.AddTransient<AsanaTokenHandler>(_ => new AsanaTokenHandler(tokenProvider));

        services.AddIAsanaApi(options =>
            {
                options.BaseAddress = new Uri("https://app.asana.com");
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                // ZeroAlloc.Rest 0.2.0 [Header] only supports method/parameter targets, not interface level.
                // Keep Accept here until the library adds class-level header support.
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<AsanaTokenHandler>()
            .AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new AsanaDataProvider(sp.GetRequiredService<IAsanaApi>(), opts));
    }
}
