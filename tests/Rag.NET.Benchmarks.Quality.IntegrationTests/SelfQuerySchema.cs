using Rag.NET.Models;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>The attribute schema the self-query runs give the model.</summary>
/// <remarks>
/// One attribute, and it is the corpus a document came from — the same choice the tag-filtered cell
/// made, for the same reason. No BEIR corpus carries tags, so any invented vocabulary would become
/// the thing being measured; the corpus is a fact about the data, which gives the model a question
/// with a knowable right answer and gives the cell a control to be read against.
/// </remarks>
internal static class SelfQuerySchema
{
    /// <summary>The single-attribute schema: which corpus a document came from.</summary>
    public static readonly IReadOnlyList<AttributeInfo> Corpus =
    [
        new(
            TagFilteredAblationRow.TagKey,
            "Which corpus the document came from. 'scifact' holds scientific claims and biomedical " +
            "abstracts; 'fiqa' holds personal-finance question answering."),
    ];
}
