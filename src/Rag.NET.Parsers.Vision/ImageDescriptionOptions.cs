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

    /// <summary>Strip prompt injection patterns from LLM output before storing.</summary>
    public bool SanitiseOutput { get; set; } = true;
}
