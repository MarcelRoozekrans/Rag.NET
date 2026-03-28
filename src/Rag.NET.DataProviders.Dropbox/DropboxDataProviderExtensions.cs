using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Dropbox;

/// <summary>DI registration extensions for <see cref="DropboxDataProvider"/>.</summary>
public static class DropboxDataProviderExtensions
{
    /// <summary>Registers using a static access token.</summary>
    public static IServiceCollection AddDropboxDataProvider(
        this IServiceCollection services,
        string accessToken,
        Action<DropboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return services.AddDropboxDataProvider(new StaticTokenProvider(accessToken), configure);
    }

    /// <summary>Registers using a custom <see cref="ITokenProvider"/> (e.g. OAuth refresh token).</summary>
    public static IServiceCollection AddDropboxDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        Action<DropboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var opts = new DropboxOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new DropboxDataProvider(tokenProvider, opts));
    }
}
