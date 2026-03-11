using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Word;

public static class WordParserBuilderExtensions
{
    public static RagBuilder AddWordParser(this RagBuilder builder)
    {
        builder.AddParser<WordDocumentParser>();
        return builder;
    }
}
