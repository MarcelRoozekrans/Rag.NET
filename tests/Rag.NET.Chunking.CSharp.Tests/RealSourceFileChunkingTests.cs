using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.CSharp.Tests;

/// <summary>
/// Chunks a real source file from this repository through Roslyn.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this project hands the strategy a short inline string. Those are precise
/// about behaviour and say nothing about whether the strategy survives production C#: file-scoped
/// namespaces, primary constructors, records, generics with constraints, pattern matching,
/// raw string literals, nested types. The strategy answers a parse failure by returning the whole
/// input as one chunk — a quiet fallback that no inline-source test would ever trip, and that would
/// turn semantic chunking into no chunking at all for a real file.
/// </para>
/// <para>
/// The file chosen is the strategy's own implementation, so the test cannot drift away from
/// representative input: whatever language features this library writes, it must also chunk.
/// </para>
/// </remarks>
public class RealSourceFileChunkingTests
{
    private const string TargetRelativePath = "src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs";

    [Fact]
    public async Task ChunkAsync_OverThisLibrarysOwnSource_SplitsItRatherThanFallingBack()
    {
        var path = Path.Combine(FindRepositoryRoot(), TargetRelativePath);
        Assert.True(File.Exists(path), $"'{TargetRelativePath}' was not found under the repository root. If the file moved, point this test at its new path — do not delete it.");

        var source = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var strategy = new CSharpChunkingStrategy(new CSharpChunkingOptions(), NullLogger<CSharpChunkingStrategy>.Instance);
        var section = new DocumentSection { Text = source, DocumentId = new DocumentId(TargetRelativePath) };

        var chunks = await strategy
            .ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The parse-failure fallback is exactly one chunk holding the whole input. Asserting on the
        // count alone would pass for a file with a single member, so the shape is asserted too.
        Assert.True(chunks.Count > 1, $"Expected the file to split into members; got {chunks.Count} chunk(s), which is what the parse-failure fallback returns.");
        Assert.DoesNotContain(chunks, c => string.Equals(c.Text, source, StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Fact]
    public async Task ChunkAsync_OverThisLibrarysOwnSource_AttributesTheRealNamespace()
    {
        var path = Path.Combine(FindRepositoryRoot(), TargetRelativePath);
        var source = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var strategy = new CSharpChunkingStrategy(new CSharpChunkingOptions(), NullLogger<CSharpChunkingStrategy>.Instance);
        var section = new DocumentSection { Text = source, DocumentId = new DocumentId(TargetRelativePath) };

        var chunks = await strategy
            .ChunkAsync(section, new ChunkingOptions(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The file declares a file-scoped namespace. Roslyn reports those through a different node
        // type than the braced form the inline-source tests use.
        Assert.Contains(chunks, c =>
            c.Metadata.TryGetValue("csharp.namespace", out var ns)
            && string.Equals(ns.ToString(), "Rag.NET.Chunking.CSharp", StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test binary until the directory holding the solution is found.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.NET.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Rag.NET.slnx was not found above the test binary, so the repository root could not be located.");
    }
}
