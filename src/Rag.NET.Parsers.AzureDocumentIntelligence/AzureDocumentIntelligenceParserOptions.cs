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
    /// The largest document, in bytes, the parser submits. Defaults to 500,000,000 bytes, Azure's
    /// documented 500 MB limit on the S0 tier read as decimal megabytes, so an oversized file fails
    /// locally instead of being uploaded to fail remotely.
    /// <para>
    /// Checked before anything is sent. The parser reads at most <c>MaxBytes + 1</c> bytes from
    /// the stream's current position, so a larger document is refused without being buffered in
    /// full, whether or not the stream can seek; a seekable stream whose remaining length already
    /// exceeds the cap is refused without being read at all. A refused document throws
    /// <see cref="AzureDocumentIntelligenceException"/> naming the file and the cap, and no
    /// request is made.
    /// </para>
    /// <para>
    /// Must be between 1 and <see cref="Array.MaxLength"/>: the document is buffered in a single
    /// array, so no larger cap could be honoured. The parser's constructor and the registration
    /// extensions reject anything outside that range.
    /// </para>
    /// </summary>
    public long MaxBytes { get; set; } = 500_000_000;
}
