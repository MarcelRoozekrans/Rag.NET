namespace Rag.NET.Api.Client.DependencyInjection;

public sealed class RagApiClientOptions
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
}
