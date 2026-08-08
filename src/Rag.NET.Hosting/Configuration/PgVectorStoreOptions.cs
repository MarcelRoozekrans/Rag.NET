namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Settings for <c>UsePgVector</c>, bound from <c>RagNet:VectorStore:PgVector</c>. Mirrors
/// <c>PgVectorBuilderExtensions.UsePgVector</c>'s single parameter: PgVector takes a connection
/// string, not a host/port/collection triple.
/// </summary>
public sealed class PgVectorStoreOptions
{
    /// <summary>The PostgreSQL connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}
