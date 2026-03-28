using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
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
        Action<BoxDataProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(jwtConfigJson);

        var config  = BoxConfig.CreateFromJsonString(jwtConfigJson);
        var session = new BoxJWTAuth(config);
        var token   = session.AdminToken();
        var client  = session.AdminClient(token);

        var opts = new BoxDataProviderOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new BoxDataProvider(client, opts));
    }

    /// <summary>Registers using an existing <see cref="BoxClient"/> instance.</summary>
    public static IServiceCollection AddBoxDataProvider(
        this IServiceCollection services,
        BoxClient boxClient,
        Action<BoxDataProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(boxClient);

        var opts = new BoxDataProviderOptions();
        configure?.Invoke(opts);
        return services.AddSingleton<IFileContentProvider>(new BoxDataProvider(boxClient, opts));
    }
}
