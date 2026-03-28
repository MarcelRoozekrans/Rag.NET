using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

public static class GmailDataProviderExtensions
{
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
