using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.PowerPoint;

public static class PowerPointParserBuilderExtensions
{
    public static RagBuilder AddPowerPointParser(this RagBuilder builder)
    {
        builder.AddParser<PowerPointDocumentParser>();
        return builder;
    }
}
