using Microsoft.Extensions.AI;

namespace Rag.NET.Parsers.Vision;

public sealed class ImageDescriptionOptions
{
    /// <summary>Optional cheaper vision model override. Null uses the DI-registered IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>LLM prompt sent with the image. {fileName} is replaced at runtime.</summary>
    public string Prompt { get; set; } =
        "Describe this image in detail, focusing on any text, data, charts, or diagrams. Image file: {fileName}";

    /// <summary>When true, attempt Tesseract OCR first. Skip the vision LLM call if OCR yields sufficient text.</summary>
    public bool TryOcrBeforeVision { get; set; } = false;

    /// <summary>Minimum OCR character count to accept OCR output and skip the vision LLM call.</summary>
    public int OcrMinCharacters { get; set; } = 50;

    /// <summary>Path to the Tesseract tessdata directory used by the OCR fallback.</summary>
    /// <remarks>
    /// Named to match <c>PdfParserOptions.TessDataPath</c>, because the two OCR paths should not
    /// need learning twice.
    /// </remarks>
    public string TessDataPath { get; set; } = "./tessdata";

    /// <summary>Tesseract language code used by the OCR fallback. Default: <c>"eng"</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Was a literal in the method body until #299.</b> <c>PdfParserOptions.OcrLanguage</c> has
    /// been settable all along, so a caller ingesting the same German document through the two
    /// parsers could configure one and not the other — and the Vision path gave no indication that it
    /// was reading English only.
    /// </para>
    /// <para>
    /// Takes any code the installed tessdata provides, including the multi-language <c>+</c> form
    /// Tesseract accepts, such as <c>"deu+eng"</c>. The matching <c>.traineddata</c> must be present
    /// in <see cref="TessDataPath"/>; Tesseract fails at engine construction when it is not, and that
    /// failure is logged and treated as "no OCR text" rather than thrown.
    /// </para>
    /// </remarks>
    public string OcrLanguage { get; set; } = "eng";

    /// <summary>Strip prompt injection patterns from LLM output before storing.</summary>
    public bool SanitiseOutput { get; set; } = true;
}
