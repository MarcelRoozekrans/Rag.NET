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

        var opts = new GitLabOptions
        {
            BaseUrl = baseUrl,
            ProjectIdOrPath = projectIdOrPath,
        };
        configure?.Invoke(opts);

        // The client is constructed after the configure callback, from the options object — a
        // callback that changed BaseUrl changes the instance targeted (issue #108 found the
        // client being built from the raw parameter before the callback even ran).
        ArgumentException.ThrowIfNullOrWhiteSpace(opts.BaseUrl, nameof(configure));
        var client = new GitLabClient(opts.BaseUrl, token);

        return services.AddSingleton<IFileContentProvider>(new GitLabDataProvider(client, opts));
    }
}
