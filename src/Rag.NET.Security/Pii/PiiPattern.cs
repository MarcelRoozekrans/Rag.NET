namespace Rag.NET.Security;

/// <summary>A regex pattern and its replacement placeholder for PII detection.</summary>
public sealed record PiiPattern
{
    /// <summary>The placeholder inserted in place of matched text, e.g. <c>[EMAIL]</c>.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The regular expression pattern. Compiled at sanitiser construction time.</summary>
    public required string RegexPattern { get; init; }
}
