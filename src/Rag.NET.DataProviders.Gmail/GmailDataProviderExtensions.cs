using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

/// <summary>Extension methods for registering <see cref="GmailDataProvider"/> with dependency injection.</summary>
public static class GmailDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="GmailDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tokenProvider">Provider that supplies the OAuth2 access token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="GmailOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddGmailDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        Action<GmailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var opts = new GmailOptions();
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(
            new GmailDataProvider(tokenProvider, opts));
    }
}
