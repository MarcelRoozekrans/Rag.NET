using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Converts email-shaped <see cref="DocumentSection"/>s — headings of <c>headers</c>,
/// <c>body</c>, or <c>attachment:&lt;name&gt;</c> — into <see cref="TextChunk"/> instances,
/// stamping <c>template=email</c> and <c>part=headers|body|attachment:&lt;name&gt;</c> metadata on
/// each chunk. Agnostic to which parser produced the sections; see
/// <see cref="RagBuilderExtensions.UseEmailChunking{TBuilder}"/>.
/// </summary>
public sealed class EmailChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
#pragma warning disable CS1998 // async method lacks await — intentional: converts sync enumerable to async stream
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return MakeChunk(section, index++);
        }
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return MakeChunk(section, 0);
    }
#pragma warning restore CS1998

    private static TextChunk MakeChunk(DocumentSection section, int index) =>
        new()
        {
            Text = section.Text,
            DocumentId = section.DocumentId,
            ChunkIndex = index,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["template"] = "email",
                ["part"] = section.Heading ?? "body",
            },
        };
}
