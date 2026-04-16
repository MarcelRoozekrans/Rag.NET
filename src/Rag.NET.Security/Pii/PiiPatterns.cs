namespace Rag.NET.Security;

/// <summary>Built-in PII patterns. Reference these to remove specific defaults from <see cref="PiiDetectionOptions"/>.</summary>
public static class PiiPatterns
{
    public static readonly PiiPattern Email = new()
    {
        Placeholder = "[EMAIL]",
        RegexPattern = @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b"
    };

    public static readonly PiiPattern Phone = new()
    {
        Placeholder = "[PHONE]",
        RegexPattern = @"(?:\+?1[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}\b"
    };

    public static readonly PiiPattern Ssn = new()
    {
        Placeholder = "[SSN]",
        RegexPattern = @"\b\d{3}-\d{2}-\d{4}\b"
    };

    public static readonly PiiPattern CreditCard = new()
    {
        Placeholder = "[CREDIT_CARD]",
        RegexPattern = @"\b(?:4\d{3}|5[1-5]\d{2}|6011|3[47]\d{2})[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}\b"
    };

    public static readonly PiiPattern IpAddress = new()
    {
        Placeholder = "[IP_ADDRESS]",
        RegexPattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b"
    };

    /// <summary>All five built-in patterns in their default order.</summary>
    public static IReadOnlyList<PiiPattern> Defaults { get; } =
        [Email, Phone, Ssn, CreditCard, IpAddress];
}
