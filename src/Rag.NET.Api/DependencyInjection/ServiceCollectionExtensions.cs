using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Api.Authentication;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Webhooks;

namespace Rag.NET.Api.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetApi(
        this IServiceCollection services,
        Action<RagApiOptions>? configure = null)
    {
        var options = new RagApiOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.Configure<ApiKeyOptions>(o => o.ApiKeys = options.ApiKeys);

        return services;
    }

    /// <summary>
    /// Registers webhook ingestion options and the default payload parser. Map the endpoint
    /// with <see cref="EndpointRouteBuilderExtensions.MapRagNetWebhooks"/>. The parser is
    /// registered with <c>TryAdd</c>: a custom <see cref="IWebhookPayloadParser"/> registered
    /// BEFORE this call wins over the built-in <see cref="GenericWebhookPayloadParser"/>.
    /// The webhook route prefix is exempted from API-key auth — webhook requests are
    /// authenticated by their HMAC signature instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Required; must set a non-empty <see cref="WebhookOptions.Secret"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <see cref="WebhookOptions.Secret"/> is empty.</exception>
    public static IServiceCollection AddRagNetWebhooks(
        this IServiceCollection services,
        Action<WebhookOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebhookOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            throw new ArgumentException(
                "WebhookOptions.Secret must be a non-empty string — it is the HMAC-SHA256 key that authenticates webhook requests.",
                nameof(configure));
        }

        services.AddSingleton(options);
        services.TryAddSingleton<IWebhookPayloadParser, GenericWebhookPayloadParser>();
        // Signature auth replaces the API key on the webhook route.
        services.Configure<ApiKeyOptions>(o => o.ExemptPathPrefixes = [.. o.ExemptPathPrefixes, options.RoutePrefix]);

        return services;
    }
}
