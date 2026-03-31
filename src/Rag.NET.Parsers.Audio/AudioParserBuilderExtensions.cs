using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Audio;

public static class AudioParserBuilderExtensions
{
    public static TBuilder AddAudioParser<TBuilder>(this TBuilder builder,
        AudioParserOptions? options = null)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton(options ?? new AudioParserOptions());
        builder.AddParser<AudioDocumentParser>();
        return builder;
    }
}
