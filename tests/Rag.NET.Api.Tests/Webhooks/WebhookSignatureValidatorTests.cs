using System.Security.Cryptography;
using System.Text;
using Rag.NET.Api.Webhooks;
using Xunit;

namespace Rag.NET.Api.Tests.Webhooks;

public sealed class WebhookSignatureValidatorTests
{
    private const string Secret = "test-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"documentId":"doc-1","content":"hello"}""");

    private static string Sign(byte[] body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void ValidUppercaseHexSignature_IsValid()
        => Assert.True(WebhookSignatureValidator.IsValid(Body, Sign(Body, Secret), Secret));

    [Fact]
    public void ValidLowercaseHexSignature_IsValid()
        => Assert.True(WebhookSignatureValidator.IsValid(
            Body, Sign(Body, Secret).ToLowerInvariant(), Secret));

    [Fact]
    public void Sha256PrefixedSignature_IsValid()
        => Assert.True(WebhookSignatureValidator.IsValid(Body, $"sha256={Sign(Body, Secret)}", Secret));

    [Fact]
    public void SignatureWithWrongSecret_IsInvalid()
        => Assert.False(WebhookSignatureValidator.IsValid(Body, Sign(Body, "other-secret"), Secret));

    [Fact]
    public void SignatureOverDifferentBody_IsInvalid()
        => Assert.False(WebhookSignatureValidator.IsValid(
            Body, Sign(Encoding.UTF8.GetBytes("tampered"), Secret), Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankSignature_IsInvalid(string? signature)
        => Assert.False(WebhookSignatureValidator.IsValid(Body, signature, Secret));

    [Fact]
    public void NonHexSignature_IsInvalid()
        => Assert.False(WebhookSignatureValidator.IsValid(Body, "not-hex-at-all!", Secret));

    [Fact]
    public void TruncatedSignature_IsInvalid()
        => Assert.False(WebhookSignatureValidator.IsValid(Body, Sign(Body, Secret)[..16], Secret));
}
