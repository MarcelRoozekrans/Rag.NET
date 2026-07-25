using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Extension methods for registering <see cref="LinearDataProvider"/> with dependency injection.</summary>
public static class LinearDataProviderExtensions
{
    /// <summary>
    /// Registers a <see cref="LinearDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apiKey">Linear personal API key (e.g. <c>lin_api_...</c>).</param>
    /// <param name="configure">Optional callback to further configure <see cref="LinearOptions"/>.</param>
    /// <param name="baseUrl">Optional base URL override (default <c>https://api.linear.app</c>); useful for tests.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddLinearDataProvider(
        this IServiceCollection services,
        string apiKey,
        Action<LinearOptions>? configure = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var opts = new LinearOptions();
        configure?.Invoke(opts);

        if (FindInvalidState(opts.States) is { } invalidState)
        {
            throw new ArgumentException(
                $"Invalid Linear state type '{invalidState}'. Valid values: " +
                $"{string.Join(", ", LinearOptions.ValidStateTypes)} " +
                "(note: Linear spells it 'canceled').",
                nameof(configure));
        }

        if (opts.PageSize <= 0)
        {
            throw new ArgumentException(
                "LinearOptions.PageSize must be greater than zero.", nameof(configure));
        }

        services.AddILinearApi(options =>
            {
                options.BaseAddress = new Uri(string.IsNullOrEmpty(baseUrl) ? "https://api.linear.app" : baseUrl);
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .ConfigureHttpClient(client =>
            {
                // Linear personal API keys are sent BARE — "Authorization: <API_KEY>" with no
                // "Bearer" prefix ("Bearer" is only used for OAuth2 access tokens). Pinned
                // 2026-07-25 per https://linear.app/developers/graphql ("pass the API key with
                // header: Authorization: <API_KEY>"). TryAddWithoutValidation because a bare
                // key is not a valid scheme-prefixed AuthenticationHeaderValue.
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey);
                // ZeroAlloc.Rest [Header] only supports method/parameter targets, not interface
                // level. Keep Accept here until the library adds class-level header support.
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
            new LinearDataProvider(
                sp.GetRequiredService<ILinearApi>(), opts,
                sp.GetService<ILogger<LinearDataProvider>>()));
    }

    /// <summary>Returns the first state value not in <see cref="LinearOptions.ValidStateTypes"/>, or <see langword="null"/>.</summary>
    private static string? FindInvalidState(IReadOnlyList<string>? states)
    {
        if (states is null)
            return null;

        for (int i = 0; i < states.Count; i++)
        {
            bool valid = false;
            for (int j = 0; j < LinearOptions.ValidStateTypes.Count; j++)
            {
                if (string.Equals(states[i], LinearOptions.ValidStateTypes[j], StringComparison.Ordinal))
                {
                    valid = true;
                    break;
                }
            }

            if (!valid)
                return states[i];
        }

        return null;
    }
}
