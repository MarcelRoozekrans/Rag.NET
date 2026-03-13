using Rag.NET.Models;

namespace Rag.NET.PostRetrieval;

public static class LostInTheMiddleReorderer
{
    /// <summary>
    /// Reorders results so the most relevant appear at the start and end of the list,
    /// with less relevant results in the middle. Exploits the "lost-in-the-middle" phenomenon
    /// (Liu et al. 2023) where LLMs attend less to content in the middle of long contexts.
    /// </summary>
    /// <param name="results">Results sorted by descending relevance score (best first).</param>
    /// <remarks>
    /// The precondition that <paramref name="results"/> must be pre-sorted in descending order
    /// is not validated — unsorted input produces meaningless output.
    /// </remarks>
    public static IReadOnlyList<SearchResult> Reorder(IReadOnlyList<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        if (results.Count <= 2)
        {
            return results.ToArray();
        }

        var reordered = new SearchResult[results.Count];
        int left = 0;
        int right = results.Count - 1;

        for (int i = 0; i < results.Count; i++)
        {
            if (i % 2 == 0)
            {
                reordered[left++] = results[i];
            }
            else
            {
                reordered[right--] = results[i];
            }
        }

        return reordered;
    }
}
