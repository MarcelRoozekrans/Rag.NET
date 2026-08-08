using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Rag.NET.Parsers.Vision;

public partial class VideoDocumentParser(
    IChatClient chatClient,
    VideoDescriptionOptions options,
    ILogger<VideoDocumentParser>? logger = null) : IDocumentParser, IDeclaresContentTypes
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "video/mp4", "video/quicktime", "video/x-matroska", "video/x-msvideo", "video/webm",
    };

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes => SupportedTypes;

    private readonly ILogger<VideoDocumentParser> _logger =
        logger ?? NullLogger<VideoDocumentParser>.Instance;

    public bool CanParse(string contentType) => SupportedTypes.Contains(contentType);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tmp");
        try
        {
            // Stream.Null is a test seam: subclasses that override ExtractScenesAsync
            // (e.g. FakeVideoDocumentParser) never call FFMpeg, so no real file is needed.
            // In production a null stream would cause FFMpeg to fail with a missing-file error.
            if (stream != Stream.Null)
            {
                var fs = File.Create(tempFile);
                await using (fs.ConfigureAwait(false))
                    await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var allScenes = await ExtractScenesAsync(tempFile, cancellationToken).ConfigureAwait(false);
            var scenes = CapScenes(allScenes, options.MaxScenes);

            var index = 0;
            foreach (var (timestampSeconds, frameBytes) in scenes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var description = await DescribeFrameAsync(
                    frameBytes, metadata.FileName, timestampSeconds, cancellationToken)
                    .ConfigureAwait(false);

                if (options.SanitiseOutput)
                    description = PromptInjectionSanitiser.Sanitise(description, _logger, metadata.FileName);

                yield return new DocumentSection
                {
                    Text = description,
                    Heading = $"video_scene_{index}",
                    DocumentId = metadata.DocumentId,
                    SectionIndex = index,
                    PageNumber = (int)timestampSeconds,
                };
                index++;
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    protected virtual async Task<IReadOnlyList<(double TimestampSeconds, byte[] FrameBytes)>> ExtractScenesAsync(
        string videoFilePath, CancellationToken ct)
    {
        var timestamps = await GetSceneTimestampsAsync(videoFilePath, ct).ConfigureAwait(false);
        var results = new List<(double, byte[])>();

        foreach (var ts in timestamps)
        {
            ct.ThrowIfCancellationRequested();
            var frameBytes = await ExtractFrameAsync(videoFilePath, ts, ct).ConfigureAwait(false);
            if (frameBytes is not null)
                results.Add((ts, frameBytes));
            else
                LogFrameExtractionFailed(_logger, ts, videoFilePath);
        }

        return results;
    }

    private async Task<IReadOnlyList<double>> GetSceneTimestampsAsync(
        string videoFilePath, CancellationToken ct)
    {
        var timestamps = new List<double>();
        var threshold = options.SceneChangeThreshold.ToString("F2", CultureInfo.InvariantCulture);

        await FFMpegArguments
            .FromFileInput(videoFilePath, false)
            .OutputToPipe(new StreamPipeSink(Stream.Null), opts => opts
                .WithCustomArgument($"-vf \"select=gt(scene\\,{threshold}),showinfo\"")
                .WithCustomArgument("-vsync 0")
                .ForceFormat("null"))
            // showinfo writes to stderr, not stdout — use NotifyOnError to capture its output
            .NotifyOnError(line =>
            {
                if (line.Contains("pts_time:", StringComparison.Ordinal))
                {
                    var start = line.IndexOf("pts_time:", StringComparison.Ordinal) + 9;
                    var end = line.IndexOf(' ', start);
                    var tsStr = end > start ? line[start..end] : line[start..];
                    if (double.TryParse(tsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))
                        timestamps.Add(ts);
                }
            })
            .CancellableThrough(ct)
            .ProcessAsynchronously().ConfigureAwait(false);

        return timestamps;
    }

    private static async Task<byte[]?> ExtractFrameAsync(
        string videoFilePath, double timestampSeconds, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var success = await FFMpegArguments
            .FromFileInput(videoFilePath, false, opts => opts
                .Seek(TimeSpan.FromSeconds(timestampSeconds)))
            .OutputToPipe(new StreamPipeSink(ms), opts => opts
                .WithFrameOutputCount(1)
                .ForceFormat("mjpeg"))
            .CancellableThrough(ct)
            .ProcessAsynchronously().ConfigureAwait(false);

        return success ? ms.ToArray() : null;
    }

    protected virtual async Task<string> DescribeFrameAsync(
        byte[] frameBytes, string fileName, double timestampSeconds, CancellationToken ct)
    {
        var activeClient = options.ChatClient ?? chatClient;
        var ts = timestampSeconds.ToString("F1", CultureInfo.InvariantCulture);
        var prompt = options.Prompt
            .Replace("{fileName}", fileName, StringComparison.Ordinal)
            .Replace("{timestamp}", ts, StringComparison.Ordinal);

        var message = new ChatMessage(ChatRole.User,
        [
            new DataContent(frameBytes, "image/jpeg"),
            new TextContent(prompt),
        ]);

        var response = await activeClient
            .GetResponseAsync([message], cancellationToken: ct)
            .ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }

    private static IReadOnlyList<(double, byte[])> CapScenes(
        IReadOnlyList<(double TimestampSeconds, byte[] FrameBytes)> scenes, int maxScenes)
    {
        if (maxScenes <= 0) throw new ArgumentOutOfRangeException(nameof(maxScenes), maxScenes, "MaxScenes must be greater than zero.");
        if (scenes.Count <= maxScenes) return scenes;

        var step = (double)scenes.Count / maxScenes;
        return Enumerable.Range(0, maxScenes)
            .Select(i => scenes[(int)(i * step)])
            .ToList();
    }

    [LoggerMessage(EventId = 276347502, EventName = "log_frame_extraction_failed", Level = LogLevel.Warning,
        Message = "Failed to extract frame at {TimestampSeconds}s from '{FileName}'.")]
    private static partial void LogFrameExtractionFailed(
        ILogger logger, double timestampSeconds, string fileName);
}
