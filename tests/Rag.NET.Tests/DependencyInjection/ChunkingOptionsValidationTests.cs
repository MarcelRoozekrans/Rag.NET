using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Registration-time validation of chunking sizing — issues #90 and #93.
/// <para>
/// The reported symptom was that <c>Overlap = -1</c> "just ran fine". It did, because validation
/// only happened on first ingestion and the reporter had not ingested yet. These pin that the
/// failure now happens at the configuring line, and that the two values which were wrong in the
/// opposite direction — a rejected <c>0</c> — are accepted.
/// </para>
/// </summary>
public class ChunkingOptionsValidationTests
{
    private static ServiceCollection NewServices() => new();

    [Fact]
    public void NegativeOverlap_ThrowsAtRegistration_NotOnFirstIngestion()
    {
        var services = NewServices();

        // Issue #90's reproduction, verbatim: no ingestion call anywhere.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseChunkingStrategy<RecursiveChunkingStrategy>(o =>
            {
                o.MaxChunkSize = 1000;
                o.Overlap = -1;
            })));

        Assert.Contains("Overlap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroOverlap_IsAccepted()
    {
        var services = NewServices();

        // Was rejected by [GreaterThan(0)] until #90. Non-overlapping chunks are a legitimate
        // configuration and every strategy supports them.
        services.AddRagNet(rag => rag.UseChunkingStrategy<RecursiveChunkingStrategy>(o =>
        {
            o.MaxChunkSize = 1000;
            o.Overlap = 0;
        }));

        var options = services.BuildServiceProvider().GetRequiredService<ChunkingOptions>();
        Assert.Equal(0, options.Overlap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxChunkSize_ThrowsAtRegistration(int maxChunkSize)
    {
        var services = NewServices();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseChunkingStrategy<RecursiveChunkingStrategy>(o =>
                o.MaxChunkSize = maxChunkSize)));

        Assert.Contains("MaxChunkSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlapNotSmallerThanChunkSize_ThrowsAtRegistration()
    {
        var services = NewServices();

        // Cross-property, and it reports as an ordinary validation failure rather than a special
        // case: ChunkingOptions.ValidateOverlapFitsChunk is a [CustomValidation] method, so the
        // generated validator raises it and every caller enforces it without knowing it exists.
        // Before this, the three strategies disagreed — TokenAware threw, FixedSize silently
        // degraded to no overlap, Recursive capped it.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseChunkingStrategy<RecursiveChunkingStrategy>(o =>
            {
                o.MaxChunkSize = 100;
                o.Overlap = 100;
            })));

        Assert.Contains("advance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidOptions_Register()
    {
        var services = NewServices();

        services.AddRagNet(rag => rag.UseChunkingStrategy<RecursiveChunkingStrategy>(o =>
        {
            o.MaxChunkSize = 1000;
            o.Overlap = 50;
        }));

        var options = services.BuildServiceProvider().GetRequiredService<ChunkingOptions>();
        Assert.Equal(1000, options.MaxChunkSize);
        Assert.Equal(50, options.Overlap);
    }

    [Fact]
    public void ZeroParentChunkSize_ThrowsAtRegistration_RatherThanHangingIngestion()
    {
        var services = NewServices();

        // Issue #93: this used to register happily and then loop forever during ingestion —
        // RecursiveChunkingStrategy advances its index by the chunk size, and FixedSize's guard
        // falls back to the same zero. A hang, with no error and no progress.
        _ = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseParentDocumentRetrieval(o => o.ParentChunkSize = 0)));
    }

    [Fact]
    public void NegativeParentOverlap_ThrowsAtRegistration()
    {
        var services = NewServices();

        // The quieter half of #93: gaps between parent spans send a child chunk to the
        // "last parent" fallback, so retrieval answers from the wrong part of the document.
        _ = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseParentDocumentRetrieval(o => o.ParentOverlap = -50)));
    }

    [Fact]
    public void TheGeneratedValidatorItselfEnforcesTheCrossPropertyRule()
    {
        // The reason ValidateOverlapFitsChunk is a [CustomValidation] method and not a separate
        // public Validate() the caller has to remember. The first version of this fix was the
        // latter, and PipelineIngestor called the generated validator without it — so the
        // ingestion path did not enforce this rule at all. Anyone holding only the validator
        // must get the same answer as the builder.
        var result = new ChunkingOptionsValidator()
            .Validate(new ChunkingOptions { MaxChunkSize = 100, Overlap = 100 });

        Assert.False(result.IsValid);

        var failures = result.Failures;
        var reported = new string[failures.Length];
        for (var i = 0; i < failures.Length; i++)
        {
            reported[i] = failures[i].PropertyName;
        }

        Assert.Contains(nameof(ChunkingOptions.Overlap), reported, StringComparer.Ordinal);
    }

    [Fact]
    public void DefaultParentDocumentOptions_Register()
    {
        var services = NewServices();

        services.AddRagNet(rag => rag.UseParentDocumentRetrieval());

        var options = services.BuildServiceProvider().GetRequiredService<ParentDocumentOptions>();
        Assert.Equal(2048, options.ParentChunkSize);
        Assert.Equal(100, options.ParentOverlap);
    }
}
