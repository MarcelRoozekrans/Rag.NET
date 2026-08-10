using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class PiiChunkSanitiserTests
{
    private static PiiChunkSanitiser Sut(Action<PiiDetectionOptions>? configure = null)
    {
        var opts = new PiiDetectionOptions();
        configure?.Invoke(opts);
        return new PiiChunkSanitiser(opts, NullLogger<PiiChunkSanitiser>.Instance);
    }

    private static readonly Dictionary<string, MetadataValue> Meta =
        new(StringComparer.Ordinal) { ["file_name"] = "test.txt" };

    [Fact]
    public void Sanitise_Email_Redacted()
    {
        var result = Sut().Sanitise("Contact us at alice@example.com for help.", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_Phone_Redacted()
    {
        var result = Sut().Sanitise("Call us at 555-867-5309.", Meta);
        Assert.Contains("[PHONE]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("555-867-5309", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_Ssn_Redacted()
    {
        var result = Sut().Sanitise("SSN: 123-45-6789", Meta);
        Assert.Contains("[SSN]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45-6789", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_CreditCard_Redacted()
    {
        var result = Sut().Sanitise("Card number 4111-1111-1111-1111 was charged.", Meta);
        Assert.Contains("[CREDIT_CARD]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("4111-1111-1111-1111", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_IpAddress_Redacted()
    {
        var result = Sut().Sanitise("Server IP is 192.168.1.1", Meta);
        Assert.Contains("[IP_ADDRESS]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NoPii_ReturnsOriginal()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        Assert.Equal(text, Sut().Sanitise(text, Meta));
    }

    [Fact]
    public void Sanitise_MultiplePiiInSameText_AllRedacted()
    {
        var result = Sut().Sanitise("Email alice@example.com, IP 10.0.0.1", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.Contains("[IP_ADDRESS]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_CustomPattern_Redacted()
    {
        var result = Sut(o => o.Patterns.Add(new PiiPattern
        {
            Placeholder = "[EMPLOYEE_ID]",
            RegexPattern = @"\bEMP-\d{6}\b"
        })).Sanitise("Employee EMP-001234 is on leave.", Meta);
        Assert.Contains("[EMPLOYEE_ID]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_RemovedBuiltIn_NotRedacted()
    {
        var email = "alice@example.com";
        var result = Sut(o => o.Patterns.Remove(PiiPatterns.Email))
            .Sanitise($"Email: {email}", Meta);
        Assert.Contains(email, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NullText_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Sut().Sanitise(null!, Meta));
    }
}
