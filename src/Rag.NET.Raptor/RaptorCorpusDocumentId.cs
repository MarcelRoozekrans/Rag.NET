namespace Rag.NET.Raptor;

/// <summary>The document id corpus-level RAPTOR summaries are stored under.</summary>
/// <remarks>
/// A corpus summary spans many documents, so there is no real document whose id it could honestly
/// carry. A URI-shaped id rather than a plausible file name, so it cannot collide with a real
/// document and reads as synthetic wherever it surfaces — the same convention, and the same
/// reasoning, as <c>GraphProjectionRebuilder.ReportDocumentId</c> (<c>graphrag://communities</c>).
/// <para>
/// It is also what makes a rebuild cheap: deleting this id through
/// <c>IVectorStore.DeleteByDocumentIdAsync</c> removes exactly the previous tree and nothing else,
/// with no interface change and no store that has to opt in.
/// </para>
/// </remarks>
public static class RaptorCorpusDocumentId
{
    /// <summary>The reserved id value.</summary>
    public const string Value = "raptor://corpus-tree";
}
