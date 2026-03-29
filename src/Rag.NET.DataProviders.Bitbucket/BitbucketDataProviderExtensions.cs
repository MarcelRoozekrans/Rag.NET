using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Bitbucket;

/// <summary>Extension methods for registering <see cref="BitbucketDataProvider"/> with dependency injection.</summary>
public static class BitbucketDataProviderExtensions
{
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

        services.AddDataProviderHttpClient("Bitbucket");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{appPassword}"));
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Bitbucket");
            http.BaseAddress = new Uri("https://api.bitbucket.org/2.0/");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            var api = RestService.For<IBitbucketApi>(http);
            return new BitbucketDataProvider(api, opts);
        });
    }
}
