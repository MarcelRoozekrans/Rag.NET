using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Cli;

/// <summary>
/// Runs the <c>ingest</c> command against an already-resolved <see cref="IRagPipeline"/>: reads
/// one file, or every file found recursively under a directory, and ingests each through the
/// pipeline.
/// </summary>
/// <remarks>
/// Takes its pipeline as a parameter rather than resolving one itself, so it is testable against a
/// fake <see cref="IRagPipeline"/> — no host, no configuration, no launching this project's
/// executable. <c>Program.cs</c> owns building the real one and stays thin.
/// </remarks>
internal static class IngestCommand
{
    /// <summary>
    /// Ingests <paramref name="path"/> — a single file, or every file found recursively under a
    /// directory — through <paramref name="pipeline"/>. A file that the pipeline rejects (no
    /// parser for its content type, a storage failure, ...) or that cannot even be opened becomes
    /// an <see cref="Error"/> entry rather than aborting the rest of the batch; only cancellation
    /// propagates.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Neither a file nor a directory exists at <paramref name="path"/>.
    /// </exception>
    public static async Task<Outcome> RunAsync(
        IRagPipeline pipeline, string path, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var files = ResolveFiles(path);
        var options = new IngestionOptions { Overwrite = overwrite };
        var documents = new List<Document>();
        var errors = new List<Error>();

        foreach (var file in files)
        {
            await IngestOneAsync(pipeline, file, options, documents, errors, cancellationToken)
                .ConfigureAwait(false);
        }

        return new Outcome(documents, errors);
    }

    private static async Task IngestOneAsync(
        IRagPipeline pipeline, string file, IngestionOptions options,
        List<Document> documents, List<Error> errors, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file);
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(Guid.NewGuid().ToString()),
            FileName = fileName,
            ContentType = ContentTypeMap.FromFileName(fileName),
        };

        Result<IngestionResult, RagError> result;
        try
        {
            using var stream = File.OpenRead(file);
            result = await pipeline
                .IngestAsync(stream, metadata, options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new Error(file, ex.Message));
            return;
        }

        if (result.IsSuccess)
        {
            documents.Add(new Document(file, result.Value.DocumentId, result.Value.ChunksStored));
        }
        else
        {
            errors.Add(new Error(file, result.Error.ToString()));
        }
    }

    private static IReadOnlyList<string> ResolveFiles(string path)
    {
        if (Directory.Exists(path))
        {
            return [.. Directory
                .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)];
        }

        if (File.Exists(path))
        {
            return [path];
        }

        throw new FileNotFoundException($"No file or directory found at '{path}'.", path);
    }

    /// <summary>The outcome of an <c>ingest</c> run: every file that succeeded, and every one that did not.</summary>
    public sealed record Outcome(IReadOnlyList<Document> Documents, IReadOnlyList<Error> Errors);

    /// <summary>One file the pipeline stored, and how many chunks it produced.</summary>
    public sealed record Document(string FilePath, string DocumentId, int ChunksStored);

    /// <summary>One file that could not be ingested, and why.</summary>
    public sealed record Error(string FilePath, string Message);
}
