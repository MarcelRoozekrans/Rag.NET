using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Asana;

/// <summary>Configuration for <see cref="AsanaDataProvider"/>.</summary>
public sealed class AsanaOptions : CloudStorageOptions
{
    /// <summary>Asana workspace GID that scopes task enumeration.</summary>
    public required string WorkspaceGid { get; set; }

    /// <summary>
    /// Optional project GID. When set, only tasks in this project are returned
    /// instead of all workspace tasks.
    /// </summary>
    public string? ProjectGid           { get; set; }
}
