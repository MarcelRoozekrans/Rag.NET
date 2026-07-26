using Pinecone;

namespace Rag.NET.Pinecone;

/// <summary>Options for <see cref="PineconeVectorStore"/>, validated eagerly in <c>UsePinecone</c>.</summary>
public sealed class PineconeOptions
{
    /// <summary>
    /// Pinecone API key. Required — Pinecone Local ignores it, but the client still
    /// demands one (any placeholder such as <c>"pclocal"</c> works there).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Serverless index holding the chunks. Required; Pinecone index names are 1–45
    /// characters of lowercase letters, digits, or <c>-</c>, starting and ending with a
    /// letter or digit (validation matches Pinecone's documented rules; the server
    /// remains authoritative).
    /// </summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>Dense embedding dimensions, used when creating the index.</summary>
    public int VectorDimensions { get; set; } = 1536;

    /// <summary>
    /// Optional Pinecone namespace scoping every upsert/query/delete — Pinecone's
    /// namespace-based collection isolation. Leave null for the default namespace.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>UsePinecone</c> registers
    /// <see cref="PineconeSparseVectorStore"/> instead: sparse vectors ride on the same
    /// records as the dense embeddings and the store serves <c>ISparseSearchable</c> —
    /// pair with <c>UseSpladeEncoder</c>. Requires the dotproduct metric
    /// (<c>CreateCollectionAsync</c> then creates dotproduct indexes; an existing cosine
    /// index fails fast at first use).
    /// </summary>
    public bool EnableSparseVectors { get; set; }

    /// <summary>
    /// Optional control-plane endpoint override for Pinecone Local, e.g.
    /// <c>http://localhost:5080</c>. An <c>http</c> scheme also disables TLS on the
    /// data-plane gRPC channels (the SDK's <c>IsTlsEnabled</c> switch, required for the
    /// emulator). Leave null for the real Pinecone service.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Cloud provider for created serverless indexes.</summary>
    public ServerlessSpecCloud Cloud { get; set; } = ServerlessSpecCloud.Aws;

    /// <summary>Region for created serverless indexes.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Upper bound on how long <c>CreateCollectionAsync</c> polls a freshly created
    /// index for readiness before failing. Serverless index creation typically takes
    /// under a minute; Pinecone Local indexes are ready immediately.
    /// </summary>
    public TimeSpan IndexReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
