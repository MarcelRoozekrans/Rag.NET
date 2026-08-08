using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// <c>UseQAPairsChunking()</c> deliberately overrides two parsers it does not, and cannot, share an
/// assembly with: core's <c>Rag.NET.Parsers.CsvDocumentParser</c> and
/// <c>Rag.NET.Parsers.Office</c>'s <c>ExcelDocumentParser</c>. Neither type is referenceable from
/// <c>Rag.NET.Chunking.Templates</c> — the first would mean referencing <c>Rag.NET</c>, the second
/// an optional package that may not be installed at all — so the override is declared by full type
/// name via <c>AddParser&lt;TParser&gt;(replacesTypeNames:)</c> rather than by <see cref="Type"/>.
/// </summary>
/// <remarks>
/// The no-op path is the one most likely to rot silently: nothing here fails loudly if
/// <c>replacesTypeNames</c> stops matching anything (a rename, a namespace move), because a name
/// that matches nothing is defined to be a no-op. These tests exercise both halves — replacing
/// something present, and replacing something absent — so a regression in either shows up here
/// rather than only once <c>Rag.NET.Parsers.Office</c> is added to a real composition root.
/// </remarks>
public sealed class QAPairsParserReplacementTests
{
    private const string CsvContentType = "text/csv";

    private const string SpreadsheetContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// <c>CsvDocumentParser</c> is not registered by default, so a container that only calls
    /// <c>UseQAPairsChunking()</c> never had anything to replace. The override must not throw
    /// looking for a type that was never there.
    /// </summary>
    [Fact]
    public void NothingRegisteredForCsv_UseQAPairsChunkingDoesNotThrow()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQAPairsChunking())
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is QAPairsDocumentParser);
        Assert.DoesNotContain(parsers, p => p is CsvDocumentParser);
    }

    /// <summary>
    /// Same no-op, for the Excel side. <c>Rag.NET.Parsers.Office</c> is not referenced by this test
    /// project at all, which is the point: <c>UseQAPairsChunking()</c> alone must not fail to find
    /// what it would have replaced had Office been installed.
    /// </summary>
    [Fact]
    public void NothingRegisteredForExcel_UseQAPairsChunkingDoesNotThrow()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseQAPairsChunking())
            .BuildServiceProvider();

        var selected = sp.GetServices<IDocumentParser>().First(p => p.CanParse(SpreadsheetContentType));
        Assert.IsType<QAPairsDocumentParser>(selected);
    }

    /// <summary>
    /// The live case: a caller who explicitly added core's <c>CsvDocumentParser</c> and then asked
    /// for QA-pairs chunking. The override must remove it — no conflict exception — and QA-pairs
    /// must win selection, not merely coexist.
    /// </summary>
    [Fact]
    public void CsvDocumentParserRegisteredFirst_QAPairsReplacesItAndWinsSelection()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.AddParser<CsvDocumentParser>();
                rag.Services.AddSingleton(ParserClaim.For<CsvDocumentParser>(
                    CsvContentType, "AddParser<CsvDocumentParser>()"));

                rag.UseQAPairsChunking();
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.DoesNotContain(parsers, p => p is CsvDocumentParser);

        var selected = parsers.First(p => p.CanParse(CsvContentType));
        Assert.IsType<QAPairsDocumentParser>(selected);
    }

    /// <summary>
    /// The Excel equivalent, using a stand-in rather than the real
    /// <c>Rag.NET.Parsers.Office</c> package: same namespace and name as the real
    /// <c>ExcelDocumentParser</c>, minted here so the by-name match is exercised across an assembly
    /// boundary without this test project taking a dependency on Office. It also declares the claim
    /// <c>AddExcelParser()</c> will declare once Task 3 lands, so this test exercises the state the
    /// ordering note in the plan is protecting against.
    /// </summary>
    [Fact]
    public void AStandInExcelParserRegisteredFirst_QAPairsReplacesItAndWinsSelection()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.AddParser<Rag.NET.Parsers.Excel.ExcelDocumentParser>();
                rag.Services.AddSingleton(ParserClaim.For<Rag.NET.Parsers.Excel.ExcelDocumentParser>(
                    SpreadsheetContentType, "AddExcelParser()"));

                rag.UseQAPairsChunking();
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.DoesNotContain(parsers, p => p is Rag.NET.Parsers.Excel.ExcelDocumentParser);

        var selected = parsers.First(p => p.CanParse(SpreadsheetContentType));
        Assert.IsType<QAPairsDocumentParser>(selected);
    }

    /// <summary>
    /// The escape hatch must not become a way to switch the whole guard off. A parser claiming
    /// <c>text/csv</c> under a type name <c>UseQAPairsChunking()</c> does not name in its
    /// replacement list is not removed, and still collides — Task 1's guard, unweakened.
    /// </summary>
    [Fact]
    public void AThirdPartyCsvParserNotNamedByTheReplacement_StillConflicts()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag =>
            {
                rag.AddParser<UnrelatedCsvStubParser>();
                rag.Services.AddSingleton(ParserClaim.For<UnrelatedCsvStubParser>(
                    CsvContentType, "AddParser<UnrelatedCsvStubParser>()"));

                rag.UseQAPairsChunking();
            }));

        Assert.Contains(CsvContentType, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A third-party CSV parser sharing nothing with core's <c>CsvDocumentParser</c>.</summary>
    private sealed class UnrelatedCsvStubParser : IDocumentParser
    {
        public bool CanParse(string contentType) =>
            string.Equals(contentType, CsvContentType, StringComparison.Ordinal);

        public IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream, DocumentMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Never expected to actually parse in this test.");
    }
}
