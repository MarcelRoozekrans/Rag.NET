using System.Reflection;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Api.Tests.Shared;

/// <summary>
/// Compares the options a caller passed with the options a server handed its pipeline, property by
/// property over every public property, so a field a remote client stops carrying fails a test
/// instead of going missing (#944). Linked into the HTTP and gRPC client test projects.
/// </summary>
internal static class OptionsAssert
{
    /// <summary>Asserts that every public property of the two values matches, recursively.</summary>
    public static void Equivalent(object? expected, object? actual, string path = "options")
    {
        switch (expected)
        {
            case null when actual is null:
                return;
            case null:
                Assert.Fail($"{path}: expected null, got {actual}.");
                return;
            case IDictionary<string, MetadataValue> filter:
                AssertFilter(filter, actual as IDictionary<string, MetadataValue>, path);
                return;
            case IList<ChatMessage> history:
                AssertHistory(history, actual as IList<ChatMessage>, path);
                return;
            case string or ValueType:
                Assert.True(Equals(expected, actual), $"{path}: expected {expected}, got {actual ?? "null"}.");
                return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            Equivalent(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}");
        }

        if (expected is RagOptions expectedOptions)
            Assert.True(AssignedFlags(expectedOptions) == AssignedFlags((RagOptions)actual),
                $"{path}: the short-form retrieval fields assigned differ, which RagPipeline checks against Retrieval.");
    }

    // RagOptions records which short-form retrieval fields were assigned, and RagPipeline rejects
    // any of them next to Retrieval. The record is private, so it is read the only way a test of
    // another assembly can; a rename fails this loudly rather than passing.
    private static int AssignedFlags(RagOptions options)
    {
        var field = typeof(RagOptions).GetField("_assigned", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RagOptions no longer has the _assigned field this check reads.");
        return Convert.ToInt32(field.GetValue(options), System.Globalization.CultureInfo.InvariantCulture);
    }

    // A null filter and an empty one both mean "no filtering"; the proto map cannot tell them apart.
    private static void AssertFilter(
        IDictionary<string, MetadataValue> expected, IDictionary<string, MetadataValue>? actual, string path)
    {
        var e = expected;
        var a = actual ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        Assert.True(e.Count == a.Count, $"{path}: expected {e.Count} entries, got {a.Count}.");
        foreach (var (key, value) in e)
        {
            Assert.True(a.TryGetValue(key, out var other), $"{path}: missing key {key}.");
            Assert.True(value.Equals(other), $"{path}[{key}]: expected {value.Kind} {value}, got {other.Kind} {other}.");
        }
    }

    // Null and empty history both omit history from the prompt.
    private static void AssertHistory(IList<ChatMessage> expected, IList<ChatMessage>? actual, string path)
    {
        Assert.True(expected.Count == (actual?.Count ?? 0), $"{path}: expected {expected.Count} turns.");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Role, actual![i].Role);
            Assert.Equal(expected[i].Text, actual[i].Text);
        }
    }

    /// <summary>A <see cref="RetrievalOptions"/> with every carried field away from its default.</summary>
    public static RetrievalOptions FullRetrieval() => new()
    {
        TopK = 7,
        MinScore = 0.25,
        MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["source"] = "crm",
            ["page"] = 3,
            ["draft"] = false,
            ["updated"] = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero),
        },
        UseHybridSearch = true,
        EnsembleOptions = new EnsembleOptions { DenseWeight = 0.7f, Bm25Weight = 0.2f, SparseWeight = 0.1f, K = 30 },
        UseSparseSearch = false,
        UseLostInTheMiddleReordering = true,
        UseRedundancyFilter = true,
        RedundancyThreshold = 0.9f,
        MaxContextTokens = 2000,
        UseMmr = true,
        MmrLambda = 0.3f,
        MmrCandidateCount = 21,
        UseMultiQuery = false,
        UseReranking = false,
        CandidateCount = 40,
        UseHyde = false,
        UseAdaptiveRetrieval = true,
        UseCrag = true,
        CragScoreThreshold = 0.6f,
        CragFallbackMode = CragFallbackMode.Append,
        UseSelfQuery = false,
        UseCacheEmbedding = false,
        UseCacheResult = false,
        UseParentDocument = false,
        UseTagRetrieval = false,
        UseTimeWeighting = false,
    };

    /// <summary>A <see cref="RagOptions"/> with every carried field set, retrieval in the full form.</summary>
    public static RagOptions FullAsk() => new()
    {
        Retrieval = FullRetrieval(),
        SystemPrompt = "Answer in Dutch.",
        SendSourcesAsToolResult = true,
        Temperature = 0.2f,
        ConversationHistory = [new ChatMessage(ChatRole.User, "Hi"), new ChatMessage(ChatRole.Assistant, "Hello")],
        SynthesisStrategy = SynthesisStrategy.MapReduce,
        MapReduceOptions = new MapReduceOptions { MapConcurrency = 2, MapPromptTemplate = "map {0}", ReducePromptTemplate = "reduce {0}" },
        RefineOptions = new RefineOptions { InitialPromptTemplate = "first {0}", RefinePromptTemplate = "refine {0}" },
        SkipCompression = true,
    };

    /// <summary>A <see cref="RagOptions"/> with every short-form retrieval field assigned.</summary>
    public static RagOptions FullShortForm() => new()
    {
        TopK = 9,
        MaxContextTokens = 1500,
        MinScore = 0.1,
        UseHybridSearch = true,
        UseLostInTheMiddleReordering = true,
        UseRedundancyFilter = true,
        RedundancyThreshold = 0.8f,
        MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["lang"] = "nl", ["year"] = 2026 },
    };

    /// <summary>Every <see cref="RagOptions"/> the round-trip tests send, by name.</summary>
    public static IEnumerable<(string Name, RagOptions Options)> AskCases() =>
    [
        ("defaults", new RagOptions()),
        ("full form", FullAsk()),
        ("short form", FullShortForm()),
        ("one short-form field at its default", new RagOptions { TopK = 5 }),
        ("retrieval only", new RagOptions { Retrieval = new RetrievalOptions { UseMmr = true } }),
    ];

    /// <summary>The <see cref="AskCases"/> entry called <paramref name="name"/>.</summary>
    public static RagOptions AskCase(string name) =>
        AskCases().Single(c => string.Equals(c.Name, name, StringComparison.Ordinal)).Options;
}
