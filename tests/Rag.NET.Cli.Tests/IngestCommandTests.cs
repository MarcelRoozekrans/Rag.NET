using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Cli.Tests;

/// <summary>
/// Covers <see cref="IngestCommand.RunAsync"/> against a <see cref="FakeRagPipeline"/> and real
/// temporary files on disk — no host, no configuration, no launched process.
/// </summary>
public sealed class IngestCommandTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ragnet-cli-tests-{Guid.NewGuid()}")).FullName;

    [Fact]
    public async Task RunAsync_SingleFile_ReturnsOneDocumentAndNoErrors()
    {
        var file = WriteFile("note.txt", "hello world");
        var pipeline = new FakeRagPipeline();

        var result = await IngestCommand.RunAsync(
            pipeline, file, overwrite: false, TestContext.Current.CancellationToken);

        var document = Assert.Single(result.Documents);
        Assert.Empty(result.Errors);
        Assert.Equal(file, document.FilePath);
        Assert.Equal(1, document.ChunksStored);
    }

    [Fact]
    public async Task RunAsync_Directory_IngestsEveryFileRecursively()
    {
        WriteFile("a.txt", "a");
        WriteFile(Path.Combine("nested", "b.md"), "b");
        var pipeline = new FakeRagPipeline();

        var result = await IngestCommand.RunAsync(
            pipeline, _root, overwrite: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Documents.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(2, pipeline.IngestCalls.Count);
    }

    [Fact]
    public async Task RunAsync_PathDoesNotExist_ThrowsFileNotFoundException()
    {
        var missing = Path.Combine(_root, "does-not-exist.txt");
        var pipeline = new FakeRagPipeline();

        await Assert.ThrowsAsync<FileNotFoundException>(() => IngestCommand.RunAsync(
            pipeline, missing, overwrite: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_PipelineReportsFailure_RecordsAnErrorInsteadOfThrowing()
    {
        var file = WriteFile("bad.bin", "binary-ish content");
        var pipeline = new FakeRagPipeline
        {
            IngestResult = _ => Result<IngestionResult, RagError>.Failure(
                new RagError.NoParserFound("application/octet-stream")),
        };

        var result = await IngestCommand.RunAsync(
            pipeline, file, overwrite: false, TestContext.Current.CancellationToken);

        Assert.Empty(result.Documents);
        var error = Assert.Single(result.Errors);
        Assert.Equal(file, error.FilePath);
        Assert.Contains("NoParserFound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MarkdownFile_PassesTheMarkdownContentTypeToThePipeline()
    {
        var file = WriteFile("readme.md", "# Title");
        var pipeline = new FakeRagPipeline();

        _ = await IngestCommand.RunAsync(pipeline, file, overwrite: false, TestContext.Current.CancellationToken);

        var call = Assert.Single(pipeline.IngestCalls);
        Assert.Equal("text/markdown", call.Metadata.ContentType);
    }

    [Fact]
    public async Task RunAsync_OverwriteFlag_IsForwardedToTheIngestionOptions()
    {
        var file = WriteFile("note.txt", "hello");
        var pipeline = new FakeRagPipeline();

        _ = await IngestCommand.RunAsync(pipeline, file, overwrite: true, TestContext.Current.CancellationToken);

        var call = Assert.Single(pipeline.IngestCalls);
        Assert.True(call.Options?.Overwrite);
    }

    [Fact]
    public async Task RunAsync_NullPipeline_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => IngestCommand.RunAsync(
            null!, WriteFile("note.txt", "hello"), overwrite: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhitespacePath_ThrowsArgumentException()
    {
        var pipeline = new FakeRagPipeline();

        await Assert.ThrowsAsync<ArgumentException>(() => IngestCommand.RunAsync(
            pipeline, "   ", overwrite: false, TestContext.Current.CancellationToken));
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
