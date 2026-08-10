using System.Globalization;
using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace Rag.NET.Parsers.Audio;

public class AudioDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    // Ordinal stated rather than defaulted (MA0002). A collection expression cannot carry a
    // comparer, so the set is constructed explicitly. Ordinal is what HashSet<string> already
    // used, so behaviour is unchanged -- these are IANA media types compared verbatim.
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
    };

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes => SupportedTypes;

    private readonly AudioParserOptions _options;

    public AudioDocumentParser(AudioParserOptions options)
    {
        _options = options;
    }

    public bool CanParse(string contentType) => SupportedTypes.Contains(contentType);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tmp");
        try
        {
            var fs = File.Create(tempFile);
            await using (fs.ConfigureAwait(false))
            {
                await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var sectionIndex = 0;
            await foreach (var segment in TranscribeAsync(tempFile, cancellationToken).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                yield return new DocumentSection
                {
                    Text = text,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    Heading = FormatTimestamp(segment.Start, segment.End),
                };
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    protected virtual async IAsyncEnumerable<SegmentData> TranscribeAsync(
        string audioFilePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
#pragma warning disable MA0011 // Enum.ToString does not use IFormatProvider
        var modelFileName = $"ggml-{_options.ModelType.ToString().ToLowerInvariant()}.bin";
#pragma warning restore MA0011
        var modelPath = Path.Combine(_options.ModelCacheDirectory, modelFileName);

        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(_options.ModelCacheDirectory);
            var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(_options.ModelType, QuantizationType.NoQuantization, ct)
                .ConfigureAwait(false);
            await using (modelStream.ConfigureAwait(false))
            {
                var fileStream = File.Create(modelPath);
                await using (fileStream.ConfigureAwait(false))
                {
                    await modelStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }
            }
        }

        using var factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder();
        if (_options.Language is not null)
            builder.WithLanguage(_options.Language);
        var processor = builder.Build();
        await using (processor.ConfigureAwait(false))
        {
            var audioStream = File.OpenRead(audioFilePath);
            await using (audioStream.ConfigureAwait(false))
            {
                await foreach (var segment in processor.ProcessAsync(audioStream, ct).ConfigureAwait(false))
                    yield return segment;
            }
        }
    }

    private static string FormatTimestamp(TimeSpan start, TimeSpan end)
    {
        return $"{FormatTime(start)} - {FormatTime(end)}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
