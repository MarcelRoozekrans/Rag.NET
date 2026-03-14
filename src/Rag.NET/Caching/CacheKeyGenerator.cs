using System.Security.Cryptography;
using System.Text;
using Rag.NET.Models.Options;

namespace Rag.NET.Caching;

internal static class CacheKeyGenerator
{
    internal static string ForEmbedding(string textToEmbed)
    {
        return "rag:embed:" + Hash(textToEmbed);
    }

    internal static string ForResult(string query, RetrievalOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(query);
        sb.Append('|').Append(options.TopK);
        sb.Append('|').Append(options.MinScore);
        sb.Append('|').Append(options.UseHybridSearch);
        sb.Append('|').Append(options.UseRedundancyFilter);
        sb.Append('|').Append(options.RedundancyThreshold);
        sb.Append('|').Append(options.UseMultiQuery);
        sb.Append('|').Append(options.UseReranking);
        sb.Append('|').Append(options.CandidateCount);
        sb.Append('|').Append(options.UseHyde);
        sb.Append('|').Append(options.UseLostInTheMiddleReordering);
        sb.Append('|').Append(options.UseParentDocument);

        if (options.MetadataFilter is { Count: > 0 })
        {
            var sortedKeys = options.MetadataFilter.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            foreach (var key in sortedKeys)
            {
                sb.Append('|').Append(key).Append('=').Append(options.MetadataFilter[key]);
            }
        }

        return "rag:result:" + Hash(sb.ToString());
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
