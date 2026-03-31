using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking.CSharp;

/// <summary>
/// Splits C# source files at AST member boundaries using Roslyn.
/// Each class, interface, method, property, etc. becomes its own <see cref="TextChunk"/>
/// with structured C#-specific metadata.
/// </summary>
public sealed partial class CSharpChunkingStrategy : IChunkingStrategy
{
    private readonly CSharpChunkingOptions _options;
    private readonly ILogger<CSharpChunkingStrategy> _logger;

    public CSharpChunkingStrategy(CSharpChunkingOptions options, ILogger<CSharpChunkingStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(section.Text))
            yield break;

        var tree = CSharpSyntaxTree.ParseText(section.Text, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // If there are parse errors, fall back to a single chunk with the raw text
        if (root.ContainsDiagnostics && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            LogParseError(_logger, section.DocumentId);
            yield return new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = 0,
            };
            yield break;
        }

        // Full member extraction — implemented in next task
        await foreach (var chunk in ExtractMembersAsync(root, section, options, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "C# parse errors in document {DocumentId}; falling back to single chunk")]
    private static partial void LogParseError(ILogger logger, DocumentId documentId);

    private static async IAsyncEnumerable<TextChunk> ExtractMembersAsync(
        SyntaxNode root,
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false); // placeholder — full impl in Task 4
        yield break;
    }
}
