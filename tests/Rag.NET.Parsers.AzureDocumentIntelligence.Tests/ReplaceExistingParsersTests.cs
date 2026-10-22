using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf;
using Rag.NET.Parsers.Vision;
using Rag.NET.Parsers.Word;
using Xunit;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

public sealed class ReplaceExistingParsersTests
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly Uri Endpoint = new("https://example.cognitiveservices.azure.com/");

    [Fact]
    public void Word_ReplacesAddWordParser()
    {
        var services = new ServiceCollection();

        services.AddRagNet(rag => rag
            .AddWordParser()
            .AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"), Replace(AzureDocumentIntelligenceFormats.Word)));

        AssertReplaced(services, typeof(WordDocumentParser), DocxContentType);
    }

    [Fact]
    public void Pdf_ReplacesFactoryRegisteredPdfParser()
    {
        var services = new ServiceCollection();

        services.AddRagNet(rag => rag
            .AddPdfParser(o => { })
            .AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"), Replace(AzureDocumentIntelligenceFormats.Pdf)));

        AssertReplaced(services, typeof(PdfDocumentParser), "application/pdf");
    }

    [Fact]
    public void Images_WithVision_ThrowsNamingTheOrphanedTypes()
    {
        var services = new ServiceCollection();
        var rag = new RagBuilder(services).UseImageDescription();
        var before = services.Count;

        var exception = Assert.Throws<InvalidOperationException>(() => rag.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), Replace(AzureDocumentIntelligenceFormats.Images)));

        Assert.Contains("image/webp", exception.Message, StringComparison.Ordinal);
        Assert.Contains("image/gif", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ImageDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, services.Count);
    }

    [Fact]
    public void ParserRegisteredAfterAzure_StillConflicts()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddRagNet(rag => rag
            .AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"), Replace(AzureDocumentIntelligenceFormats.Word))
            .AddWordParser()));

        Assert.Contains(typeof(WordDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            typeof(AzureDocumentIntelligenceDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_DoesNotReplace()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddRagNet(rag => rag
            .AddWordParser()
            .AddAzureDocumentIntelligenceParser(
                Endpoint, new AzureKeyCredential("key"), o => o.Formats = AzureDocumentIntelligenceFormats.Word)));

        Assert.Contains(typeof(WordDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceWithNothingToReplace_RegistersNormally()
    {
        var services = new ServiceCollection();

        services.AddRagNet(rag => rag.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), Replace(AzureDocumentIntelligenceFormats.Word)));

        var claim = Assert.Single(Claims(services), c => string.Equals(c.ContentType, DocxContentType, StringComparison.Ordinal));
        Assert.Equal(typeof(AzureDocumentIntelligenceDocumentParser).FullName, claim.ParserTypeName);
        Assert.Null(claim.ReplacesParserTypeName);
        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IDocumentParser>(), p => p is AzureDocumentIntelligenceDocumentParser);
    }

    [Fact]
    public void ClaimCaseIsIgnored()
    {
        var services = new ServiceCollection();

        services.AddRagNet(rag =>
        {
            rag.AddParserFactory(_ => new UpperCaseTiffParser());
            rag.Services.AddSingleton(ParserClaim.For<UpperCaseTiffParser>("IMAGE/TIFF", "AddUpperCaseTiffParser"));
            rag.AddAzureDocumentIntelligenceParser(
                Endpoint, new AzureKeyCredential("key"), o => o.ReplaceExistingParsers = true);
        });

        AssertReplaced(services, typeof(UpperCaseTiffParser), "image/tiff");
    }

    private static Action<AzureDocumentIntelligenceParserOptions> Replace(AzureDocumentIntelligenceFormats formats) =>
        o =>
        {
            o.Formats = formats;
            o.ReplaceExistingParsers = true;
        };

    private static void AssertReplaced(ServiceCollection services, Type replaced, string contentType)
    {
        using var provider = services.BuildServiceProvider();
        var parsers = provider.GetServices<IDocumentParser>().ToList();
        Assert.DoesNotContain(parsers, p => p.GetType() == replaced);
        Assert.Contains(parsers, p => p is AzureDocumentIntelligenceDocumentParser);

        var claim = Assert.Single(Claims(services), c => string.Equals(c.ContentType, contentType, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(AzureDocumentIntelligenceDocumentParser).FullName, claim.ParserTypeName);
        Assert.Equal(replaced.FullName, claim.ReplacesParserTypeName);
    }

    private static List<ParserClaim> Claims(ServiceCollection services) =>
        [.. services
            .Where(d => d.ServiceType == typeof(ParserClaim))
            .Select(d => (ParserClaim)d.ImplementationInstance!)];

    private sealed class UpperCaseTiffParser : IDocumentParser
    {
        public bool CanParse(string contentType) =>
            string.Equals(contentType, "image/tiff", StringComparison.OrdinalIgnoreCase);

        public async IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream,
            DocumentMetadata metadata,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
