using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// A parser that claims a content type and then fails on it — the shape
/// <see cref="ContainerEntryDispatcher"/>'s containment exists for, and the one a third-party
/// <see cref="IDocumentParser"/> can take without the email package knowing anything about it.
/// </summary>
/// <param name="contentType">The content type this parser claims.</param>
/// <param name="sectionsBeforeThrow">
/// How many sections to yield before throwing. A non-zero value is the case a naive containment
/// gets wrong: those sections have already reached the caller and must survive the failure.
/// </param>
/// <param name="exceptionFactory">
/// Builds the exception to throw. Defaults to an <see cref="InvalidOperationException"/>; a test
/// that needs cancellation to stay cancellation supplies an
/// <see cref="OperationCanceledException"/>.
/// </param>
internal sealed class ThrowingDocumentParser(
    string contentType,
    int sectionsBeforeThrow = 0,
    Func<Exception>? exceptionFactory = null) : IDocumentParser
{
    /// <summary>The text of every section yielded before the failure.</summary>
    public const string YieldedText = "content produced before the parser failed";

    public bool CanParse(string candidate) =>
        candidate.Equals(contentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        throw exceptionFactory?.Invoke()
            ?? new InvalidOperationException($"Failed to parse '{metadata.FileName}'.");
    }
}
