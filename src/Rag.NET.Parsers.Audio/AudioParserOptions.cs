using Whisper.net.Ggml;

namespace Rag.NET.Parsers.Audio;

public sealed class AudioParserOptions
{
    public GgmlType ModelType           { get; init; } = GgmlType.Base;
    public string?  Language            { get; init; }
    public string   ModelCacheDirectory { get; init; } = Path.GetTempPath();
}
