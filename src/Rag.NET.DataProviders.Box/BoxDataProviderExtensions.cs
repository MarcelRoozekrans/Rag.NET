using Box.Sdk.Gen;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

/// <summary>DI registration extensions for <see cref="BoxDataProvider"/>.</summary>
public static class BoxDataProviderExtensions
{
    /// <summary>Registers using Box JWT service account config JSON.</summary>
    public static IServiceCollection AddBoxDataProvider(
        this IServiceCollection services,
        string jwtConfigJson,
        Action<BoxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(jwtConfigJson);

        var opts = new BoxOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(_ =>
        {
            var config = JwtConfig.FromConfigJsonString(jwtConfigJson);
            var auth   = new BoxJwtAuth(config);
            var client = new BoxClient(auth);
            return new BoxDataProvider(client, opts);
        });
    }

    /// <summary>Registers using an existing <see cref="BoxClient"/> instance.</summary>
    public static IServiceCollection AddBoxDataProvider(
        this IServiceCollection services,
        BoxClient boxClient,
        Action<BoxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(boxClient);

        var opts = new BoxOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new BoxDataProvider(boxClient, opts));
    }
}
