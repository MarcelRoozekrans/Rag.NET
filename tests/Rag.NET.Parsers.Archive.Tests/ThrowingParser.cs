using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// A parser that claims a content type and then fails on it — the shape
/// <see cref="ContainerEntryDispatcher"/>'s containment exists for, and the one a third-party
/// <see cref="IDocumentParser"/> can take without this package knowing anything about it.
/// </summary>
/// <param name="contentType">The content type this parser claims.</param>
/// <param name="sectionsBeforeThrow">
/// How many sections to yield before throwing. Zero is the plain case; a non-zero value is the one a
/// naive containment gets wrong, since those sections have already reached the caller.
/// </param>
/// <param name="failure">
/// The exception to throw instead of the default <see cref="InvalidOperationException"/>, for a test
/// about how a particular kind of failure is treated.
/// </param>
/// <param name="throwBeforeEnumerating">
/// Throw from <see cref="ParseAsync"/> itself, before an enumerator exists, rather than from the
/// first <c>MoveNextAsync</c> — the other of the two places the dispatcher contains a failure.
/// </param>
internal sealed class ThrowingParser(
    string contentType,
    int sectionsBeforeThrow = 0,
    Exception? failure = null,
    bool throwBeforeEnumerating = false) : IDocumentParser
{
    /// <summary>The message of the exception thrown, so a test can tell it from any other failure.</summary>
    public const string FailureMessage = "this entry's parser failed";

    /// <summary>The text of every section yielded before the failure.</summary>
    public const string YieldedText = "content produced before the parser failed";

    public bool CanParse(string candidate) =>
        candidate.Equals(contentType, StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (throwBeforeEnumerating)
            throw Failure();

        return ParseSectionsAsync(metadata, cancellationToken);
    }

    private Exception Failure() => failure ?? new InvalidOperationException(FailureMessage);

    private async IAsyncEnumerable<DocumentSection> ParseSectionsAsync(
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < sectionsBeforeThrow; i++)
        {
            await Task.Yield();
            yield return new DocumentSection
            {
                Text = YieldedText,
                DocumentId = metadata.DocumentId,
                SectionIndex = i,
            };
        }

        await Task.Yield();
        throw Failure();
    }
}
