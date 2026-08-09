using Microsoft.Extensions.AI;

namespace Rag.NET.Parsers.Vision;

public sealed class VideoDescriptionOptions
{
    /// <summary>Optional cheaper vision model override. Null uses the DI-registered IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>LLM prompt. {fileName} and {timestamp} are replaced at runtime.</summary>
    public string Prompt { get; set; } =
        "Describe this video frame in detail, noting any visible text, actions, or context. File: {fileName}, timestamp: {timestamp}s";

    /// <summary>
    /// FFmpeg scene detection sensitivity (0.0–1.0); <c>UseVideoDescription</c> rejects a value
    /// outside that range at registration time. Lower = more scenes detected.
    /// </summary>
    public double SceneChangeThreshold { get; set; } = 0.3;

    /// <summary>Maximum number of scenes to extract per video. Evenly-spaced subset taken if over cap.</summary>
    public int MaxScenes { get; set; } = 50;

    /// <summary>Strip prompt injection patterns from LLM descriptions before storing.</summary>
    public bool SanitiseOutput { get; set; } = true;
}
