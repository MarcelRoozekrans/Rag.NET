namespace Rag.NET.Ingestion;

/// <summary>Thrown by ParseBehavior when no parser is registered for the content type.</summary>
#pragma warning disable RCS1194 // Implement exception constructors — non-standard primary arg (contentType)
public sealed class NoParserFoundException : InvalidOperationException
#pragma warning restore RCS1194
{
    /// <summary>Gets the content type for which no parser was registered.</summary>
    public string ContentType { get; }

    /// <summary>Initializes a new instance with the content type that had no registered parser.</summary>
    public NoParserFoundException(string contentType)
        : base($"No parser registered for content type '{contentType}'.")
    {
        ContentType = contentType;
    }

    /// <summary>Initializes a new instance with the content type, a custom message, and an inner exception.</summary>
    public NoParserFoundException(string contentType, string message, Exception innerException)
        : base(message, innerException)
    {
        ContentType = contentType;
    }
}
