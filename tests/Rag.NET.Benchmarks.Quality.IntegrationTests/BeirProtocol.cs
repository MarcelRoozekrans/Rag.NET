namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Which of the two protocols a measurement runs under.
/// </summary>
/// <remarks>
/// Exists so <see cref="BeirRunBudget"/> can key a cost on the pair that actually determines it. A
/// dataset does not have "a cost": SciFact costs ~5 minutes under
/// <see cref="Parity"/> and roughly four times that under <see cref="Real"/>, because the real
/// protocol embeds 56,707 chunks where parity embeds 5,183 documents. Keying the budget on the
/// dataset alone would have to pick one of those two numbers and be wrong about the other.
/// </remarks>
public enum BeirProtocol
{
    /// <summary>
    /// One chunk per document, truncated at the model's 256 tokens — BEIR's own protocol, and the
    /// only one comparable to a published figure. Measured by <see cref="BeirParityTests"/>.
    /// </summary>
    Parity,

    /// <summary>
    /// Rag.NET's own chunking, max-pooled back to documents, measured against the parity run rather
    /// than against anything published. Measured by <see cref="BeirRealChunkingTests"/>, which runs
    /// <b>both</b> legs — so a real case costs its own embedding work plus whatever the parity leg
    /// costs when the cache cannot supply it.
    /// </summary>
    Real,
}
