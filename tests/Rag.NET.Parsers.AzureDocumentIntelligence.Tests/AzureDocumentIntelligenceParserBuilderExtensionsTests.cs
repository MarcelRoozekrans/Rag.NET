using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers.Excel;
using Rag.NET.Parsers.PowerPoint;
using Rag.NET.Parsers.Word;
using Xunit;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

public sealed class AzureDocumentIntelligenceParserBuilderExtensionsTests
{
    private static readonly Uri Endpoint = new("https://example.cognitiveservices.azure.com/");

    [Fact]
    public void Register_Defaults_ClaimsExactlyTiffAndHeif()
    {
        var services = new ServiceCollection();

        new RagBuilder(services).AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"));

        var claims = Claims(services);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "image/tiff", "image/heif", "image/heic" },
            claims.Select(c => c.ContentType).ToHashSet(StringComparer.Ordinal));
        Assert.All(claims, c =>
        {
            Assert.Equal(typeof(AzureDocumentIntelligenceDocumentParser).FullName, c.ParserTypeName);
            Assert.Equal("AddAzureDocumentIntelligenceParser", c.RegistrationMethod);
        });
    }

    [Fact]
    public void Register_OptedInFormats_ClaimsEachOfTheirContentTypes()
    {
        var services = new ServiceCollection();

        new RagBuilder(services).AddAzureDocumentIntelligenceParser(
            Endpoint,
            new AzureKeyCredential("key"),
            o => o.Formats = AzureDocumentIntelligenceFormats.Pdf | AzureDocumentIntelligenceFormats.Html);

        Assert.Equal(
            ["application/pdf", "text/html"],
            Claims(services).Select(c => c.ContentType),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Register_WordAlongsideAddWordParser_ThrowsAtAddRagNet()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddRagNet(rag => rag
            .AddWordParser()
            .AddAzureDocumentIntelligenceParser(
                Endpoint,
                new AzureKeyCredential("key"),
                o => o.Formats = AzureDocumentIntelligenceFormats.Word)));

        Assert.Contains(typeof(WordDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            typeof(AzureDocumentIntelligenceDocumentParser).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_DefaultsAlongsideAddWordParser_DoNotConflict()
    {
        var services = new ServiceCollection();

        var exception = Record.Exception(() => services.AddRagNet(rag => rag
            .AddWordParser()
            .AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"))));

        Assert.Null(exception);
        var claimed = Claims(services).ToLookup(c => c.ParserTypeName, c => c.ContentType, StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "image/tiff", "image/heif", "image/heic" },
            claimed[typeof(AzureDocumentIntelligenceDocumentParser).FullName!].ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            WordDocumentParser.ContentTypes, claimed[typeof(WordDocumentParser).FullName!], StringComparer.Ordinal);
    }

    [Fact]
    public void OfficeContentTypes_MatchTheOfficeParsersOwnLists()
    {
        // The OOXML strings are copied, not referenced, so the shipped package stays free of
        // Rag.NET.Parsers.Office. The conflict guard compares strings, so a drifted copy would
        // let both parsers register for one format without a word.
        Assert.Equal(WordDocumentParser.ContentTypes, AzureDocumentIntelligenceFormats.Word.ContentTypes());
        Assert.Equal(ExcelDocumentParser.ContentTypes, AzureDocumentIntelligenceFormats.Excel.ContentTypes());
        Assert.Equal(
            PowerPointDocumentParser.ContentTypes, AzureDocumentIntelligenceFormats.PowerPoint.ContentTypes());
    }

    [Fact]
    public void Register_NoFormats_Throws()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), o => o.Formats = AzureDocumentIntelligenceFormats.None));

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains(nameof(AzureDocumentIntelligenceParserOptions.Formats), exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Register_NonPositiveMaxBytes_Throws(long maxBytes)
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddAzureDocumentIntelligenceParser(
            Endpoint, new StubTokenCredential(), o => o.MaxBytes = maxBytes));

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains(nameof(AzureDocumentIntelligenceParserOptions.MaxBytes), exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Register_MaxBytesBeyondTheLargestBuffer_Throws()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), o => o.MaxBytes = Array.MaxLength + 1L));

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains(nameof(AzureDocumentIntelligenceParserOptions.MaxBytes), exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);

        builder.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), o => o.MaxBytes = Array.MaxLength);
        Assert.NotEmpty(services);
    }

    [Fact]
    public void Register_InvalidAnalyzerSettings_ThrowAtRegistration()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddAzureDocumentIntelligenceParser(
            Endpoint, new AzureKeyCredential("key"), o => o.Analyzer.PricePerPage = -1m));

        Assert.Contains(
            nameof(AzureDocumentIntelligenceAnalyzerSettings.PricePerPage), exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Register_KeyCredential_ResolvesTheParserFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICostLedger>(new FakeCostLedger());

        new RagBuilder(services).AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key"));

        using var provider = services.BuildServiceProvider();
        var parser = Assert.IsType<AzureDocumentIntelligenceDocumentParser>(
            Assert.Single(provider.GetServices<IDocumentParser>()));
        Assert.True(parser.CanParse("image/tiff"));
    }

    [Fact]
    public void Register_TokenCredential_ResolvesTheParserFromTheContainer()
    {
        var services = new ServiceCollection();

        new RagBuilder(services).AddAzureDocumentIntelligenceParser(
            Endpoint, new StubTokenCredential(), o => o.Formats = AzureDocumentIntelligenceFormats.Pdf);

        using var provider = services.BuildServiceProvider();
        var parser = Assert.IsType<AzureDocumentIntelligenceDocumentParser>(
            Assert.Single(provider.GetServices<IDocumentParser>()));
        Assert.True(parser.CanParse("application/pdf"));
        Assert.False(parser.CanParse("image/tiff"));
    }

    [Fact]
    public void Register_NullArguments_Throw()
    {
        var builder = new RagBuilder(new ServiceCollection());

        Assert.Throws<ArgumentNullException>(() =>
            ((RagBuilder)null!).AddAzureDocumentIntelligenceParser(Endpoint, new AzureKeyCredential("key")));
        Assert.Throws<ArgumentNullException>(() =>
            ((RagBuilder)null!).AddAzureDocumentIntelligenceParser(Endpoint, new StubTokenCredential()));
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAzureDocumentIntelligenceParser(null!, new AzureKeyCredential("key")));
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAzureDocumentIntelligenceParser(null!, new StubTokenCredential()));
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAzureDocumentIntelligenceParser(Endpoint, (AzureKeyCredential)null!));
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAzureDocumentIntelligenceParser(Endpoint, (TokenCredential)null!));
    }

    private static List<ParserClaim> Claims(ServiceCollection services) =>
        [.. services
            .Where(d => d.ServiceType == typeof(ParserClaim))
            .Select(d => (ParserClaim)d.ImplementationInstance!)];

    /// <summary>Hand-written credential: these tests resolve the parser, they never call out.</summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("stub-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
