using Rag.NET.Parsers;
using Rag.NET.Parsers.Excel;
using Xunit;

namespace Rag.NET.Parsers.Office.Tests;

/// <summary>
/// <c>Rag.NET.Chunking.Templates</c>'s <c>RagBuilderExtensions</c> overrides
/// <c>Rag.NET.Parsers.CsvDocumentParser</c> and <c>Rag.NET.Parsers.Excel.ExcelDocumentParser</c> by
/// full type name — literal strings, because that project can reference neither <c>Rag.NET</c> (it
/// would be circular) nor <c>Rag.NET.Parsers.Office</c> (an optional package it must not require).
/// Its own test project mints a same-named stand-in to exercise the by-name match, which proves the
/// override logic works but nothing about whether the literals actually name the real types — a
/// rename would leave the constant, the stand-in and that test all agreeing with each other and all
/// wrong. This project references both real types, so it is the one place that can assert it.
/// </summary>
public sealed class ParserTypeNameContractTests
{
    [Fact]
    public void CsvDocumentParserFullNameMatchesTheStringLiteralQAPairsChunkingOverrides()
    {
        Assert.Equal("Rag.NET.Parsers.CsvDocumentParser", typeof(CsvDocumentParser).FullName);
    }

    [Fact]
    public void ExcelDocumentParserFullNameMatchesTheStringLiteralQAPairsChunkingOverrides()
    {
        Assert.Equal(
            "Rag.NET.Parsers.Excel.ExcelDocumentParser", typeof(ExcelDocumentParser).FullName);
    }
}
