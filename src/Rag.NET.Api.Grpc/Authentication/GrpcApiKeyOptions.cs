namespace Rag.NET.Api.Grpc.Authentication;

internal sealed class GrpcApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];
}
