using Microsoft.Extensions.DependencyInjection;
using NGitLab;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GitLab;

/// <summary>DI registration extensions for <see cref="GitLabDataProvider"/>.</summary>
public static class GitLabDataProviderExtensions
{
    /// <summary>Registers a <see cref="GitLabDataProvider"/> backed by a private-token client.</summary>
    public static IServiceCollection AddGitLabDataProvider(
        this IServiceCollection services,
        string baseUrl,
        string projectIdOrPath,
        string token,
        Action<GitLabOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectIdOrPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var client = new GitLabClient(baseUrl, token);
        var opts = new GitLabOptions
        {
            BaseUrl = baseUrl,
            ProjectIdOrPath = projectIdOrPath,
        };
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(new GitLabDataProvider(client, opts));
    }
}
