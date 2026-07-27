namespace Rag.NET.Models;

#pragma warning disable RCS1194 // Implement exception constructors — Key/ProviderId/EntryId are the
// primary diagnostic values; a message-only instance would carry misleading empty defaults.

/// <summary>
/// Thrown at ingest time when a data-provider connector emits a metadata key the framework
/// reserves for itself (see <see cref="ReservedMetadataKeys"/>).
/// <para>
/// Deliberately a throw rather than a per-entry <c>Result</c> failure: connector tag keys are
/// string literals in connector code, so a collision is a deterministic authoring bug that
/// would repeat identically for every document in the run. A <c>Result</c> failure would emit
/// N copies of one bug and still let a corrupted ranking ship. This is a programming error,
/// not a data error, so it surfaces on the first document.
/// </para>
/// </summary>
public sealed class ReservedMetadataKeyException : InvalidOperationException
{
    /// <param name="key">The reserved key the connector emitted.</param>
    /// <param name="providerId">The provider that emitted it.</param>
    /// <param name="entryId">The entry the collision was first observed on.</param>
    public ReservedMetadataKeyException(string key, string providerId, string entryId)
        : base(BuildMessage(key, providerId, entryId))
    {
        Key = key;
        ProviderId = providerId;
        EntryId = entryId;
    }

    /// <summary>The reserved key the connector emitted.</summary>
    public string Key { get; }

    /// <summary>The provider that emitted <see cref="Key"/>.</summary>
    public string ProviderId { get; }

    /// <summary>The entry the collision was first observed on.</summary>
    public string EntryId { get; }

    private static string BuildMessage(string key, string providerId, string entryId) =>
        $"Data provider '{providerId}' emitted the reserved metadata key '{key}' on entry '{entryId}'. " +
        $"Reserved keys are written by the framework itself and a connector tag would shadow them " +
        $"(connector tags are applied first, with TryAdd), silently corrupting retrieval. " +
        $"Rename the connector tag. Reserved keys: {string.Join(", ", ReservedMetadataKeys.All)}.";
}

#pragma warning restore RCS1194
