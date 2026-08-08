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
    ILogger<ImageDocumentParser>? logger = null) : IDocumentParser, IDeclaresContentTypes
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp",
    };

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes => SupportedTypes;

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

        var description = options.TryOcrBeforeVision
            ? TryOcr(imageBytes, fileName)
                ?? await DescribeImageAsync(imageBytes, fileName, metadata.ContentType ?? "application/octet-stream", cancellationToken).ConfigureAwait(false)
            : await DescribeImageAsync(imageBytes, fileName, metadata.ContentType ?? "application/octet-stream", cancellationToken).ConfigureAwait(false);

        if (options.SanitiseOutput)
            description = PromptInjectionSanitiser.Sanitise(description, _logger, fileName);

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
#if ENABLE_OCR
        try
        {
            using var engine = new Tesseract.TesseractEngine(@"./tessdata", "eng", Tesseract.EngineMode.Default);
            using var pix = Tesseract.Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(pix);
            var text = page.GetText()?.Trim() ?? string.Empty;
            return text.Length >= options.OcrMinCharacters ? text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOcrFailed(_logger, fileName, ex);
            return null;
        }
#else
        throw new InvalidOperationException(
            "OCR support requires the Tesseract package. Add <EnableOcr>true</EnableOcr> to your project file to enable it.");
#endif
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    [LoggerMessage(EventId = 1703571814, EventName = "log_ocr_failed", Level = LogLevel.Warning,
        Message = "OCR failed for '{FileName}'; falling back to vision LLM.")]
    private static partial void LogOcrFailed(ILogger logger, string fileName, Exception ex);
}
