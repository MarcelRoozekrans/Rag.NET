using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class CodeChunkingStrategyTests
{
    private static DocumentSection Section(string text, string docId = "file.py") =>
        new() { Text = text, DocumentId = new DocumentId(docId) };

    private static ChunkingOptions Opts(int max = 200) =>
        new() { MaxChunkSize = max, Overlap = 0 };

    [Fact]
    public async Task Python_SplitsAtDefBoundary_NotAtNewline()
    {
        var ct   = TestContext.Current.CancellationToken;
        // Each def block is ~30 chars; MaxChunkSize=40 fits one def block but not two.
        // If \n were used (lower priority), each line would be its own chunk (many more chunks).
        // If \ndef  is used (correct), exactly 2 chunks are produced.
        var code = "def foo():\n    x = 1\ndef bar():\n    y = 2";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section(code, "script.py"), Opts(40), ct)
                              .ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("def foo", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("def bar", chunks[1].Text, StringComparison.Ordinal);
        // Verify each def block is intact (not split at inner \n)
        Assert.Contains("x = 1", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("y = 2", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeScript_SplitsAtFunctionBoundary()
    {
        var ct   = TestContext.Current.CancellationToken;
        var code = "function greet() {\n  return 'hi';\n}\nfunction farewell() {\n  return 'bye';\n}";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section(code, "index.ts"), Opts(60), ct)
                              .ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("greet", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("farewell", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeScript_SplitsAtInterfaceBoundary()
    {
        var ct   = TestContext.Current.CancellationToken;
        // interface keyword is TypeScript-specific; JS does not have this separator
        var code = "interface Foo {\n  name: string;\n}\ninterface Bar {\n  value: number;\n}";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section(code, "types.ts"), Opts(50), ct)
                              .ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("Foo", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Bar", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Go_SplitsAtFuncBoundary()
    {
        var ct   = TestContext.Current.CancellationToken;
        var code = "func Hello() {}\nfunc World() {}";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section(code, "main.go"), Opts(30), ct)
                              .ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task UnknownExtension_FallsBackToGenericSeparators()
    {
        var ct   = TestContext.Current.CancellationToken;
        var code = "block one\n\nblock two\n\nblock three";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions());

        var chunks = await sut.ChunkAsync(Section(code, "script.xyz"), Opts(20), ct)
                              .ToListAsync(ct);

        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public async Task ExplicitLanguageOverride_UsedRegardlessOfExtension()
    {
        var ct   = TestContext.Current.CancellationToken;
        // File has .txt extension but Language = "python" is set
        var code = "def foo():\n    pass\ndef bar():\n    return 1";
        var sut  = new CodeChunkingStrategy(new CodeChunkingOptions { Language = "python" });

        var chunks = await sut.ChunkAsync(Section(code, "script.txt"), Opts(50), ct)
                              .ToListAsync(ct);

        // Python separators applied despite .txt extension
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void UnrecognisedLanguage_ThrowsAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new CodeChunkingStrategy(new CodeChunkingOptions { Language = "brainfuck" }));
    }

    [Fact]
    public async Task EmptySection_YieldsNoChunks()
    {
        var ct  = TestContext.Current.CancellationToken;
        var sut = new CodeChunkingStrategy(new CodeChunkingOptions());
        var chunks = await sut.ChunkAsync(Section(""), Opts(), ct).ToListAsync(ct);
        Assert.Empty(chunks);
    }
}
