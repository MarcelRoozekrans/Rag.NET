namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>Options for <see cref="AzureDocumentIntelligenceDocumentParser"/>.</summary>
public sealed class AzureDocumentIntelligenceParserOptions
{
    /// <summary>Model, pricing, polling and locale settings for the underlying analyzer.</summary>
    public AzureDocumentIntelligenceAnalyzerSettings Analyzer { get; } = new();

    /// <summary>
    /// The formats the parser claims. Defaults to <see cref="AzureDocumentIntelligenceFormats.Tiff"/>
    /// | <see cref="AzureDocumentIntelligenceFormats.Heif"/>, the formats no local parser reads;
    /// every other format is an opt-in that sends the document to Azure and bills it.
    /// </summary>
    public AzureDocumentIntelligenceFormats Formats { get; set; } =
        AzureDocumentIntelligenceFormats.Tiff | AzureDocumentIntelligenceFormats.Heif;

    /// <summary>
    /// The largest document, in bytes, the parser submits. Defaults to 500 MiB, matching Azure's
    /// own 500 MB limit on the S0 tier, so an oversized file can fail locally instead of being
    /// uploaded to fail remotely.
    /// </summary>
    public long MaxBytes { get; set; } = 500L * 1024 * 1024;
}
