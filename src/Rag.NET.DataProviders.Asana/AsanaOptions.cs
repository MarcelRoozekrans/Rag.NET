using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

public sealed class AsanaOptions : CloudStorageOptions
{
    public required string WorkspaceGid { get; init; }
    public string? ProjectGid           { get; init; }
}
