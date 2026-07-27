using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers.Pdf.Ocr;
using Xunit;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

public sealed class AzureDocumentIntelligenceBuilderExtensionsTests
{
    private static readonly Uri Endpoint = new("https://example.cognitiveservices.azure.com/");

    [Fact]
    public void UseAzureDocumentIntelligenceOcr_KeyCredential_ResolvesTheEngine()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddPdfParser().UseAzureDocumentIntelligenceOcr(Endpoint, new AzureKeyCredential("key"));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureDocumentIntelligenceOcrEngine>(provider.GetRequiredService<IDocumentOcrEngine>());
    }

    [Fact]
    public void UseAzureDocumentIntelligenceOcr_TokenCredential_ResolvesTheEngine()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddPdfParser().UseAzureDocumentIntelligenceOcr(Endpoint, new StubTokenCredential());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureDocumentIntelligenceOcrEngine>(provider.GetRequiredService<IDocumentOcrEngine>());
    }

    [Fact]
    public void UseAzureDocumentIntelligenceOcr_ConfiguredEngine_ReachesTheParser()
    {
        // Registration goes through UseDocumentOcrEngine's factory overload, so the engine is
        // constructed from the container — which is how it picks up ICostLedger and ILogger
        // without either appearing on the extension's signature.
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        var ledger = new FakeCostLedger();
        services.AddSingleton<ICostLedger>(ledger);

        builder.AddPdfParser().UseAzureDocumentIntelligenceOcr(
            Endpoint, new AzureKeyCredential("key"), o => o.PricePerPage = 0.01m);

        using var provider = services.BuildServiceProvider();
        var parser = Assert.IsType<PdfDocumentParser>(
            Assert.Single(provider.GetServices<IDocumentParser>()));
        Assert.NotNull(parser);
        Assert.IsType<AzureDocumentIntelligenceOcrEngine>(provider.GetRequiredService<IDocumentOcrEngine>());
    }

    [Fact]
    public void UseAzureDocumentIntelligenceOcr_WithTesseractFallback_ThrowsInEitherOrder()
    {
        // The wrapper is thin on purpose: because it delegates to UseDocumentOcrEngine it
        // inherits the both-engines guard, and inherits it in both registration orders rather
        // than only the one the wrapper happens to exercise.
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        builder.AddPdfParser(o => o.UseOcrFallback = true);

        var tesseractFirst = Assert.Throws<InvalidOperationException>(() =>
            builder.UseAzureDocumentIntelligenceOcr(Endpoint, new AzureKeyCredential("key")));
        Assert.Contains("UseDocumentOcrEngine", tesseractFirst.Message, StringComparison.Ordinal);

        var reversedServices = new ServiceCollection();
        var reversedBuilder = new RagBuilder(reversedServices);
        reversedBuilder.UseAzureDocumentIntelligenceOcr(Endpoint, new AzureKeyCredential("key"));

        var azureFirst = Assert.Throws<InvalidOperationException>(() =>
            reversedBuilder.AddPdfParser(o => o.UseOcrFallback = true));
        Assert.Contains("UseDocumentOcrEngine", azureFirst.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseAzureDocumentIntelligenceOcr_NullArguments_Throw()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        Assert.Throws<ArgumentNullException>(() =>
            builder.UseAzureDocumentIntelligenceOcr(null!, new AzureKeyCredential("key")));
        Assert.Throws<ArgumentNullException>(() =>
            builder.UseAzureDocumentIntelligenceOcr(Endpoint, (AzureKeyCredential)null!));
        Assert.Throws<ArgumentNullException>(() =>
            builder.UseAzureDocumentIntelligenceOcr(Endpoint, (TokenCredential)null!));
        Assert.Throws<ArgumentException>(() =>
            builder.UseAzureDocumentIntelligenceOcr(Endpoint, new AzureKeyCredential("key"), o => o.ModelId = " "));
    }

    /// <summary>Hand-written credential: the DI tests resolve the engine, they never call out.</summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("stub-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
