namespace Rag.NET.Ingestion;

#pragma warning disable RCS1194 // Implement exception constructors — non-standard primary arg (contentType)
public sealed class NoParserFoundException(string contentType)
    : InvalidOperationException($"No parser registered for content type '{contentType}'.")
{
    public string ContentType { get; } = contentType;
}
#pragma warning restore RCS1194
