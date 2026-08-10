namespace Rag.NET.Models;

/// <summary>
/// The runtime type a <see cref="MetadataValue"/> carries. Deliberately small: these four cover
/// every store's native filter types, and adding a case later is cheap, so no speculative kinds.
/// </summary>
public enum MetadataValueKind
{
    /// <summary>A text value — also what every value written before metadata carried types reads back as.</summary>
    String = 0,

    /// <summary>
    /// A numeric value, stored as an IEEE 754 <see cref="double"/>. A single numeric kind rather
    /// than separate integer/floating kinds because that is the strongest guarantee every vector
    /// store can honour (Pinecone and Azure AI Search store all metadata numbers as doubles) —
    /// a distinct integer kind would promise precision two stores cannot keep. Integers up to
    /// 2^53 round-trip exactly.
    /// </summary>
    Number = 1,

    /// <summary>A boolean value.</summary>
    Boolean = 2,

    /// <summary>
    /// A point in time. Serialised as UTC ISO-8601, so the instant round-trips exactly; the
    /// original offset does not (two values naming the same instant are equal and stored
    /// identically).
    /// </summary>
    DateTimeOffset = 3,
}
