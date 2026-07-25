using System.Security.Cryptography;
using System.Text;

namespace Rag.NET;

/// <summary>
/// Deterministic GUID per <c>(DocumentId, ChunkIndex)</c>: SHA-256 of
/// <c>"{documentId}\n{chunkIndex}"</c> truncated to 16 bytes with the RFC 9562 version-8
/// (custom) and RFC 4122 variant bits set. Shared by the Qdrant (sparse) and Weaviate
/// stores so dense upserts, sparse vector updates, and re-ingestion all address the same
/// record — re-storing a chunk replaces it instead of duplicating it.
/// <para>
/// PERSISTENT STORAGE CONTRACT: existing Qdrant collections and Weaviate classes hold
/// records addressed by these ids — the algorithm must never change (pinned by unit tests
/// in both store test suites).
/// </para>
/// </summary>
internal static class DeterministicChunkId
{
    public static Guid Derive(string documentId, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}\n{chunkIndex}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(guidBytes);
    }
}
