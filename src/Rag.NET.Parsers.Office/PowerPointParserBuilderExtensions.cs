using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.PowerPoint;

public static class PowerPointParserBuilderExtensions
{
    public static TBuilder AddPowerPointParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<PowerPointDocumentParser>();
        return builder;
    }
}
