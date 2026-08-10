using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Converts email-shaped <see cref="DocumentSection"/>s — headings of <c>headers</c>,
/// <c>body</c>, or <c>attachment:&lt;name&gt;</c> — into <see cref="TextChunk"/> instances,
/// stamping <c>template=email</c> and <c>part=headers|body|attachment:&lt;name&gt;</c> metadata on
/// each chunk. <see cref="EmailChunkingOptions"/> filters which sections become chunks:
/// <c>headers</c> sections are skipped when <see cref="EmailChunkingOptions.IncludeHeaders"/>
/// is <see langword="false"/>, <c>attachment:*</c> sections when
/// <see cref="EmailChunkingOptions.IncludeAttachments"/> is <see langword="false"/>.
/// Agnostic to which parser produced the sections; see
/// <see cref="RagBuilderExtensions.UseEmailChunking{TBuilder}"/>.
/// </summary>
public sealed class EmailChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly EmailChunkingOptions _options;

    /// <param name="options">
    /// Section filters; <see langword="null"/> uses the defaults, which include every section.
    /// </param>
    public EmailChunkingStrategy(EmailChunkingOptions? options = null)
        => _options = options ?? new EmailChunkingOptions();

#pragma warning disable CS1998 // async method lacks await — intentional: converts sync enumerable to async stream
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!ShouldInclude(section)) continue;
            yield return MakeChunk(section, index++);
        }
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!ShouldInclude(section)) yield break;
        yield return MakeChunk(section, 0);
    }
#pragma warning restore CS1998

    /// <summary>
    /// Applies <see cref="EmailChunkingOptions.IncludeHeaders"/> and
    /// <see cref="EmailChunkingOptions.IncludeAttachments"/> by heading. Headings are matched
    /// ordinally against the parser contract this type documents — <c>headers</c> and
    /// <c>attachment:&lt;name&gt;</c> — so an unrecognised heading is always chunked.
    /// </summary>
    private bool ShouldInclude(DocumentSection section)
    {
        if (!_options.IncludeHeaders &&
            string.Equals(section.Heading, "headers", StringComparison.Ordinal))
        {
            return false;
        }

        if (!_options.IncludeAttachments &&
            section.Heading is { } heading &&
            heading.StartsWith("attachment:", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static TextChunk MakeChunk(DocumentSection section, int index)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["template"] = "email",
            ["part"] = section.Heading ?? "body",
        };
        PageMetadata.Write(metadata, section.PageNumber, section.PageNumber);

        return new TextChunk
        {
            Text = section.Text,
            DocumentId = section.DocumentId,
            ChunkIndex = index,
            Metadata = metadata,
        };
    }
}
