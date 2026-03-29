using AirtableApiClient;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Airtable;

/// <summary>Extension methods for registering <see cref="AirtableDataProvider"/> with dependency injection.</summary>
public static class AirtableDataProviderExtensions
{
    /// <summary>
    /// Registers an <see cref="AirtableDataProvider"/> as an <see cref="IFileContentProvider"/> singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseId">Airtable base ID (e.g. <c>appXXXXXXXXXXXXXX</c>).</param>
    /// <param name="tableName">Name or ID of the table to read.</param>
    /// <param name="personalAccessToken">Airtable personal access token.</param>
    /// <param name="configure">Optional callback to further configure <see cref="AirtableOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddAirtableDataProvider(
        this IServiceCollection services,
        string baseId,
        string tableName,
        string personalAccessToken,
        Action<AirtableOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);

        var opts = new AirtableOptions { BaseId = baseId, TableName = tableName };
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Airtable");

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var airtableBase = new AirtableBase(personalAccessToken, baseId);
            var client = new AirtableClientWrapper(airtableBase);
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Airtable");
            return new AirtableDataProvider(client, http, opts);
        });
    }
}
