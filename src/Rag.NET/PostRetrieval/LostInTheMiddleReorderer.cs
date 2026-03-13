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
    public static IReadOnlyList<SearchResult> Reorder(IReadOnlyList<SearchResult> results)
    {
        if (results.Count <= 2)
        {
            return results;
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
