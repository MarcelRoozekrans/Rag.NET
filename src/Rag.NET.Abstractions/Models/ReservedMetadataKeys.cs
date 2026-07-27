using System.Collections.Frozen;

namespace Rag.NET.Models;

/// <summary>
/// The chunk-metadata keys the framework writes itself. A data-provider connector must never
/// emit one of these from <c>FileHandle.Metadata</c>/<c>FileEntry.Metadata</c>.
/// <para>
/// The collision matters because <c>MetadataBehavior</c> applies connector tags <b>first</b>,
/// with <c>TryAdd</c> — so a connector tag named <c>created_at</c> does not lose to the
/// framework value, it <b>shadows</b> it, and <c>TimeWeightedRetriever</c> then ranks on
/// connector data with no warning.
/// </para>
/// <para>
/// This type is the single source of truth for these literals. The consumers that used to
/// declare their own copies — <c>ParentChunkKeyHelper.ParentKeyMetadata</c>,
/// <c>TimeWeightedRetriever.CreatedAtKey</c>, <c>MetadataBehavior</c> and the
/// <c>Rag.NET.Security</c> retrieval guards — all live in assemblies that reference
/// <c>Rag.NET.Abstractions</c>, so they now alias these constants rather than re-typing them.
/// A duplicated literal that drifts is exactly the bug the reserved-key guard exists to catch.
/// </para>
/// </summary>
public static class ReservedMetadataKeys
{
    /// <summary>Document identifier, written by <c>MetadataBehavior</c> from <see cref="DocumentMetadata.DocumentId"/>.</summary>
    public const string DocumentId = "document_id";

    /// <summary>Source file name, written by <c>MetadataBehavior</c> from <see cref="DocumentMetadata.FileName"/>.</summary>
    public const string FileName = "file_name";

    /// <summary>Creation timestamp, written by <c>MetadataBehavior</c> and read by <c>TimeWeightedRetriever</c>.</summary>
    public const string CreatedAt = "created_at";

    /// <summary>Identifier of the <see cref="ProviderId"/> a document was ingested from, written centrally at ingest time.</summary>
    public const string ProviderId = "provider_id";

    /// <summary>Parent-chunk lookup key, written by the parent/child chunking strategy. Framework-internal.</summary>
    public const string ParentKey = "_parentKey";

    /// <summary>Roles permitted to retrieve a chunk, enforced by <c>RbacRetrievalGuard</c>.</summary>
    public const string AllowedRoles = "allowed_roles";

    /// <summary>Trust classification of a chunk, enforced by <c>TrustLevelRetrievalGuard</c>.</summary>
    public const string TrustLevel = "trust_level";

    // Held as FrozenSet, not IReadOnlySet: IsReserved runs once per connector tag per document,
    // and calling Contains through the interface would forfeit the specialised, inlineable
    // implementation that is the entire reason for freezing the set.
    private static readonly FrozenSet<string> AllKeys = new[]
    {
        DocumentId,
        FileName,
        CreatedAt,
        ProviderId,
        ParentKey,
        AllowedRoles,
        TrustLevel,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every reserved key, ordinal-compared.</summary>
    public static IReadOnlySet<string> All => AllKeys;

    /// <summary>True when <paramref name="key"/> is one the framework writes itself.</summary>
    public static bool IsReserved(string key) => AllKeys.Contains(key);
}
