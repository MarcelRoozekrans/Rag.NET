namespace Rag.NET.Api.Grpc.Client.DependencyInjection;

public sealed class RagGrpcClientOptions
{
    public required string BaseUrl { get; set; }
    public required string ApiKey { get; set; }
}
