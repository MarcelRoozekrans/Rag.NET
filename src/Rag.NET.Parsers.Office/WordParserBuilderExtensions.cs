using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Word;

public static class WordParserBuilderExtensions
{
    public static TBuilder AddWordParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<WordDocumentParser>();
        return builder;
    }
}
