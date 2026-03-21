namespace Rag.NET.SelfQuery;

internal sealed record SelfQueryOutput(
    string Query,
    IReadOnlyList<KeyValuePair<string, string>> Filters);
