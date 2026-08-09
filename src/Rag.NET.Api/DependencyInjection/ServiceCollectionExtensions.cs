using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Api.Authentication;
using Rag.NET.Api.Contracts;
using Rag.NET.Api.Webhooks;

namespace Rag.NET.Api.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the REST API. Authentication must be decided explicitly: configure at least
    /// one key in <see cref="RagApiOptions.ApiKeys"/>, or opt out with
    /// <see cref="RagApiOptions.AllowAnonymous"/> — neither (an accidentally open API) and
    /// both (a contradiction) fail here, so misconfiguration fails at registration time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="RagApiOptions"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="RagApiOptions.ApiKeys"/> is empty while
    /// <see cref="RagApiOptions.AllowAnonymous"/> is <see langword="false"/>, and when both
    /// are set at once.
    /// </exception>
    public static IServiceCollection AddRagNetApi(
        this IServiceCollection services,
        Action<RagApiOptions>? configure = null)
    {
        var options = new RagApiOptions();
        configure?.Invoke(options);

        if (options.ApiKeys.Length == 0 && !options.AllowAnonymous)
        {
            throw new InvalidOperationException(
                "AddRagNetApi: RagApiOptions.ApiKeys is empty and RagApiOptions.AllowAnonymous is false — " +
                "this would serve every endpoint unauthenticated by accident. Either configure at least one " +
                "key (o => o.ApiKeys = [\"...\"]) or opt out of authentication explicitly " +
                "(o => o.AllowAnonymous = true).");
        }

        if (options.ApiKeys.Length > 0 && options.AllowAnonymous)
        {
            throw new InvalidOperationException(
                "AddRagNetApi: RagApiOptions.AllowAnonymous is true but RagApiOptions.ApiKeys is non-empty — " +
                "the two contradict each other. Remove AllowAnonymous to require a key, or remove the keys " +
                "to serve anonymously.");
        }

        services.AddSingleton(options);
        services.Configure<ApiKeyOptions>(o =>
        {
            o.ApiKeys = options.ApiKeys;
            o.AllowAnonymous = options.AllowAnonymous;
        });

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

        // Guard the API-key exemption: an empty prefix would exempt EVERY route, and a
        // trailing slash would break PathString.StartsWithSegments matching.
        var routePrefix = options.RoutePrefix?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(routePrefix) || !routePrefix.StartsWith('/'))
        {
            throw new ArgumentException(
                "WebhookOptions.RoutePrefix must be a non-empty path starting with '/' (e.g. \"/rag/webhooks\").",
                nameof(configure));
        }

        options.RoutePrefix = routePrefix;
        services.AddSingleton(options);
        services.TryAddSingleton<IWebhookPayloadParser, GenericWebhookPayloadParser>();
        // Signature auth replaces the API key on the webhook route.
        services.Configure<ApiKeyOptions>(o => o.ExemptPathPrefixes = [.. o.ExemptPathPrefixes, routePrefix]);

        return services;
    }
}
