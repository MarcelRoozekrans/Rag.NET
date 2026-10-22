namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>Maps <see cref="AzureDocumentIntelligenceFormats"/> to the content types they claim.</summary>
public static class AzureDocumentIntelligenceFormatsExtensions
{
    // The OOXML types match the static ContentTypes lists of the Office parsers. Copied rather
    // than referenced so this package does not depend on Rag.NET.Parsers.Office.
    private const string WordContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string PowerPointContentType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    /// <summary>
    /// Returns the media types, lower-case and without parameters, that <paramref name="formats"/>
    /// claims, in flag order.
    /// </summary>
    /// <param name="formats">The formats to expand.</param>
    /// <returns>The claimed content types; empty for <see cref="AzureDocumentIntelligenceFormats.None"/>.</returns>
    public static IReadOnlyList<string> ContentTypes(this AzureDocumentIntelligenceFormats formats)
    {
        var types = new List<string>();
        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Tiff))
        {
            types.Add("image/tiff");
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Heif))
        {
            types.Add("image/heif");
            types.Add("image/heic");
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Pdf))
        {
            types.Add("application/pdf");
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Images))
        {
            types.Add("image/png");
            types.Add("image/jpeg");
            types.Add("image/jpg");
            types.Add("image/bmp");
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Word))
        {
            types.Add(WordContentType);
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Excel))
        {
            types.Add(ExcelContentType);
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.PowerPoint))
        {
            types.Add(PowerPointContentType);
        }

        if (formats.HasFlag(AzureDocumentIntelligenceFormats.Html))
        {
            types.Add("text/html");
        }

        return types;
    }
}
