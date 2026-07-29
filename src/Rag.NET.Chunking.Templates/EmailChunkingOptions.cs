namespace Rag.NET.Chunking.Templates;

public sealed class EmailChunkingOptions
{
    public bool IncludeHeaders { get; set; } = true;
    public bool IncludeAttachments { get; set; } = true;

    /// <summary>
    /// Whether <c>UseEmailChunking()</c> also registers <see cref="EmailTemplateDocumentParser"/>
    /// and its <see cref="Abstractions.ParserClaim"/>. Defaults to <see langword="true"/>; set it
    /// to <see langword="false"/> to take the chunking strategy alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The escape hatch exists because <c>UseEmailChunking()</c> registers two independent things —
    /// a parser and a chunking strategy — and only the parser collides with
    /// <c>Rag.NET.Parsers.Email</c>. Both claim <c>message/rfc822</c>, which Phase 3.11 made a
    /// startup error; without this flag that error told the user to "register only one of them"
    /// while offering no way to keep the email-shaped chunking and let the other package parse.
    /// </para>
    /// <para>
    /// The chunking strategy is unaffected either way: it consumes
    /// <see cref="Models.DocumentSection"/>s and does not care which parser produced them.
    /// </para>
    /// </remarks>
    public bool RegisterParser { get; set; } = true;
}
