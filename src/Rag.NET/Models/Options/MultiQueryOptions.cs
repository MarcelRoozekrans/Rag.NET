namespace Rag.NET.Models.Options;

public sealed class MultiQueryOptions
{
    public int VariantCount { get; set; } = 3;

    public string PromptTemplate { get; set; } =
        "Generate {count} different phrasings of the following question.\n" +
        "Return only the rephrased questions, one per line, with no numbering or extra text.\n\n" +
        "Question: {query}";
}
