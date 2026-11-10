using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models.Options;

/// <summary>
/// The eight <see cref="RagOptions"/> retrieval fields, each assigned its own default value, so a
/// test can prove that assigning a field counts even when the value does not change.
/// </summary>
internal static class RagOptionsRetrievalFieldCases
{
    public static TheoryData<string> Names =>
    [
        nameof(RagOptions.TopK),
        nameof(RagOptions.MaxContextTokens),
        nameof(RagOptions.MinScore),
        nameof(RagOptions.UseHybridSearch),
        nameof(RagOptions.UseLostInTheMiddleReordering),
        nameof(RagOptions.UseRedundancyFilter),
        nameof(RagOptions.RedundancyThreshold),
        nameof(RagOptions.MetadataFilter),
    ];

    public static RagOptions WithFieldAtDefault(string name) => name switch
    {
        nameof(RagOptions.TopK) => new RagOptions { TopK = 5 },
        nameof(RagOptions.MaxContextTokens) => new RagOptions { MaxContextTokens = null },
        nameof(RagOptions.MinScore) => new RagOptions { MinScore = 0.0 },
        nameof(RagOptions.UseHybridSearch) => new RagOptions { UseHybridSearch = false },
        nameof(RagOptions.UseLostInTheMiddleReordering) => new RagOptions { UseLostInTheMiddleReordering = false },
        nameof(RagOptions.UseRedundancyFilter) => new RagOptions { UseRedundancyFilter = false },
        nameof(RagOptions.RedundancyThreshold) => new RagOptions { RedundancyThreshold = 0.95f },
        nameof(RagOptions.MetadataFilter) => new RagOptions { MetadataFilter = null },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a RagOptions retrieval field."),
    };
}
