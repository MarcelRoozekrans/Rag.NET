using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class TrustLevelRetrievalGuardTests
{
    private static SearchResult MakeResult(string? trustLevel, string docId = "doc1")
    {
        var chunk = new TextChunk
        {
            Text = "Some text.", DocumentId = new DocumentId(docId), ChunkIndex = 0,
        };
        if (trustLevel is not null)
            chunk.Metadata["trust_level"] = trustLevel;
        return new SearchResult { Score = 1.0, Chunk = chunk };
    }

    private static TrustLevelRetrievalGuard Sut(bool dropUntrusted = true, bool warnOnExternal = true) =>
        new(new TrustLevelGuardOptions
        {
            DropUntrusted = dropUntrusted,
            WarnOnExternal = warnOnExternal,
        }, NullLogger<TrustLevelRetrievalGuard>.Instance);

    [Fact]
    public void Inspect_UntrustedChunk_DroppedWhenDropUntrustedTrue()
    {
        var results = new[] { MakeResult("untrusted"), MakeResult("internal") };
        var inspected = Sut().Inspect(results);
        Assert.Single(inspected);
        Assert.True(inspected[0].Chunk.Metadata.TryGetValue("trust_level", out var tl) ? string.Equals(tl, "internal", StringComparison.Ordinal) : true);
    }

    [Fact]
    public void Inspect_UntrustedChunk_KeptWhenDropUntrustedFalse()
    {
        var results = new[] { MakeResult("untrusted") };
        var inspected = Sut(dropUntrusted: false).Inspect(results);
        Assert.Single(inspected);
    }

    [Fact]
    public void Inspect_ExternalChunk_KeptButWarns()
    {
        var results = new[] { MakeResult("external") };
        var inspected = Sut().Inspect(results);
        Assert.Single(inspected); // kept
    }

    [Fact]
    public void Inspect_InternalChunk_PassesThrough()
    {
        var results = new[] { MakeResult("internal") };
        var inspected = Sut().Inspect(results);
        Assert.Single(inspected);
    }

    [Fact]
    public void Inspect_MissingTrustLevel_TreatedAsInternal()
    {
        var results = new[] { MakeResult(null) };
        var inspected = Sut().Inspect(results);
        Assert.Single(inspected);
    }

    [Fact]
    public void Inspect_UntrustedAmongMany_OnlyUntrustedDropped()
    {
        var results = new[]
        {
            MakeResult("internal", "a"),
            MakeResult("untrusted", "b"),
            MakeResult("external", "c"),
        };
        var inspected = Sut().Inspect(results);
        Assert.Equal(2, inspected.Count);
        Assert.DoesNotContain(inspected, r => string.Equals(r.Chunk.DocumentId.Value, "b", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_AllInternal_ReturnsOriginalList()
    {
        var results = new List<SearchResult>
        {
            MakeResult("internal"), MakeResult("internal"),
        }.AsReadOnly();
        var inspected = Sut().Inspect(results);
        Assert.Same(results, inspected); // no allocation
    }

    private sealed class TestLogger : ILogger<TrustLevelRetrievalGuard>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Inspect_ExternalChunk_LogsWarningWithDocumentId()
    {
        var logger = new TestLogger();
        var sut = new TrustLevelRetrievalGuard(new TrustLevelGuardOptions(), logger);
        sut.Inspect([MakeResult("external", "my-doc-456")]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("my-doc-456", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_UntrustedChunk_LogsWarningWithDocumentId()
    {
        var logger = new TestLogger();
        var sut = new TrustLevelRetrievalGuard(new TrustLevelGuardOptions(), logger);
        sut.Inspect([MakeResult("untrusted", "dropped-doc")]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("dropped-doc", StringComparison.Ordinal));
    }
}
