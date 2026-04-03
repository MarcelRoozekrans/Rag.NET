using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Runtime.CompilerServices;

namespace Rag.NET.Parsers.Vision;

public partial class ImageDocumentParser(
    IChatClient chatClient,
    ImageDescriptionOptions options,
    ILogger<ImageDocumentParser>? logger = null) : IDocumentParser
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp",
    };

    private readonly ILogger<ImageDocumentParser> _logger =
        logger ?? NullLogger<ImageDocumentParser>.Instance;

    public bool CanParse(string contentType) =>
        SupportedTypes.Contains(contentType);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var imageBytes = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);
        var fileName = metadata.FileName;

        string description;

        if (options.TryOcrBeforeVision)
        {
            var ocrText = TryOcr(imageBytes, fileName);
            if (ocrText is not null)
            {
                description = ocrText;
                goto yield_section;
            }
        }

        description = await DescribeImageAsync(imageBytes, fileName, metadata.ContentType ?? "application/octet-stream", cancellationToken).ConfigureAwait(false);

        yield_section:
        if (options.SanitiseOutput)
            description = PromptInjectionSanitiser.Sanitise(description);

        yield return new DocumentSection
        {
            Text = description,
            Heading = "image_description",
            DocumentId = metadata.DocumentId,
            SectionIndex = 0,
        };
    }

    protected virtual async Task<string> DescribeImageAsync(
        byte[] imageBytes, string fileName, string contentType, CancellationToken ct)
    {
        var activeClient = options.ChatClient ?? chatClient;
        var prompt = options.Prompt.Replace("{fileName}", fileName, StringComparison.Ordinal);

        var message = new ChatMessage(ChatRole.User,
        [
            new DataContent(imageBytes, contentType),
            new TextContent(prompt),
        ]);

        var response = await activeClient
            .GetResponseAsync([message], cancellationToken: ct)
            .ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }

    protected virtual string? TryOcr(byte[] imageBytes, string fileName)
    {
        try
        {
            // Tesseract is an optional dependency — throw a clear error if not installed.
            using var engine = new Tesseract.TesseractEngine(@"./tessdata", "eng", Tesseract.EngineMode.Default);
            using var ms = new MemoryStream(imageBytes);
            using var pix = Tesseract.Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix);
            var text = page.GetText()?.Trim() ?? string.Empty;
            return text.Length >= options.OcrMinCharacters ? text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOcrFailed(_logger, fileName, ex);
            return null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OCR failed for '{FileName}'; falling back to vision LLM.")]
    private static partial void LogOcrFailed(ILogger logger, string fileName, Exception ex);
}
