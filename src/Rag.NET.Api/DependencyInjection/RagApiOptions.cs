namespace Rag.NET.Api.DependencyInjection;

public sealed class RagApiOptions
{
    public string[] ApiKeys { get; set; } = [];
    public string RoutePrefix { get; set; } = "/rag";
}
