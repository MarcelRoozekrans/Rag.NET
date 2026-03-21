using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Audio;

public static class AudioParserBuilderExtensions
{
    public static RagBuilder AddAudioParser(this RagBuilder builder,
        AudioParserOptions? options = null)
    {
        builder.Services.AddSingleton(options ?? new AudioParserOptions());
        builder.AddParser<AudioDocumentParser>();
        return builder;
    }
}
