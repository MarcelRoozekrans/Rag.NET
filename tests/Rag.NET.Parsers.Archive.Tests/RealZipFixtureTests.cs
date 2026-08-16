using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Archive;
using Xunit;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// Parses a ZIP that <b>this repository did not write with the library that reads it</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(a) of the phase design).</b> Every other fixture in this project
/// is built in memory with <c>System.IO.Compression.ZipArchive</c> — the same type
/// <see cref="ZipDocumentParser"/> reads with. That proves the library round-trips itself and
/// nothing more, which is exactly the fake-wearing-a-real-extension the phase design rejects, and
/// the same defect class already corrected for the Office parsers.
/// </para>
/// <para>
/// <b>Provenance of <c>Resources/sample.zip</c>:</b> written 2026-08-16 by <b>CPython 3.14.5's
/// <c>zipfile</c></b> — an independent implementation of the ZIP format, not .NET's. Three entries,
/// one at the root and two under different directory prefixes, with deterministic timestamps
/// (2026-08-16 12:00:00) so the fixture is byte-stable if regenerated. It is 491 bytes; the
/// generating script is in this file's git history and reproduced in the commit message.
/// </para>
/// <para>
/// The interesting part is not that a ZIP parses. It is that a ZIP whose central directory,
/// compression choices, entry ordering and external attributes were all decided by a different
/// implementation parses — those are precisely the fields the class doc for
/// <see cref="ZipDocumentParser"/> notes are "written by whoever built the archive".
/// </para>
/// </remarks>
public sealed class RealZipFixtureTests
{
    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("real-zip-1"),
        FileName = "sample.zip",
        ContentType = "application/zip",
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
    };

    private static Stream OpenFixture()
    {
        var assembly = typeof(RealZipFixtureTests).Assembly;
        const string name = "Rag.NET.Parsers.Archive.Tests.Resources.sample.zip";
        return assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
    }

    private static ZipDocumentParser BuildParser()
    {
        var parsers = new List<IDocumentParser>();
        var zip = new ZipDocumentParser(parsers, options: new ArchiveParserOptions());
        parsers.Add(zip);
        parsers.Add(new PlainTextParser());
        return zip;
    }

    [Fact]
    public async Task AZipWrittenByCPython_YieldsEveryEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = OpenFixture();

        var sections = await BuildParser().ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));

        var text = string.Join("\n", sections.Select(s => s.Text));
        Assert.Contains("Integration Test Document", text, StringComparison.Ordinal);
        Assert.Contains("Second Entry", text, StringComparison.Ordinal);
        Assert.Contains("Third Entry", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Two of the three entries sit under directory prefixes (<c>notes/</c>, <c>data/</c>). A
    /// parser that only handled root-level entries, or that tripped on the separator a foreign
    /// writer chose, would drop them — and every in-memory fixture in this project writes its
    /// entries the way .NET writes them.
    /// </remarks>
    [Fact]
    public async Task EntriesUnderDirectoryPrefixes_AreNotDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = OpenFixture();

        var sections = await BuildParser().ParseAsync(stream, CreateMetadata(), ct).ToListAsync(ct);
        var text = string.Join("\n", sections.Select(s => s.Text));

        Assert.Contains("A nested path", text, StringComparison.Ordinal);
        Assert.Contains("Alice, 30, Paris", text, StringComparison.Ordinal);
    }

    /// <summary>Minimal text parser, so the zip's entries have somewhere to be parsed to.</summary>
    private sealed class PlainTextParser : IDocumentParser
    {
        public bool CanParse(string contentType) =>
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

        public async IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream,
            DocumentMetadata metadata,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(cancellationToken);
            yield return new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                SectionIndex = 0,
            };
        }
    }
}
