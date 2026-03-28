using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Rag.NET.DataProviders;

/// <summary>Registers a named HttpClient with standard resilience for connector packages.</summary>
public static class DataProviderHttpClientExtensions
{
    public static IHttpStandardResiliencePipelineBuilder AddDataProviderHttpClient(
        this IServiceCollection services,
        string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return services.AddHttpClient(name).AddStandardResilienceHandler();
    }
}
