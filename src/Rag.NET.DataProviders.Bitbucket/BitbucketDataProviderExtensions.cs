using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Bitbucket;

/// <summary>Extension methods for registering <see cref="BitbucketDataProvider"/> with dependency injection.</summary>
public static class BitbucketDataProviderExtensions
{
    private const string HttpClientName = "Bitbucket";

    /// <summary>
    /// Registers a <see cref="BitbucketDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="workspace">Bitbucket workspace slug.</param>
    /// <param name="repoSlug">Repository slug.</param>
    /// <param name="username">Bitbucket username for Basic authentication.</param>
    /// <param name="appPassword">Bitbucket app password.</param>
    /// <param name="configure">Optional callback to further configure <see cref="BitbucketOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBitbucketDataProvider(
        this IServiceCollection services,
        string workspace,
        string repoSlug,
        string username,
        string appPassword,
        Action<BitbucketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPassword);

        var opts = new BitbucketOptions { Workspace = workspace, RepoSlug = repoSlug };
        configure?.Invoke(opts);

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{username}:{appPassword}"));

        services.AddIBitbucketApi(options =>
            {
                options.BaseAddress = new Uri("https://api.bitbucket.org");
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler();

        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.bitbucket.org");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }).AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var api = sp.GetRequiredService<IBitbucketApi>();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return new BitbucketDataProvider(api, http, opts);
        });
    }
}
