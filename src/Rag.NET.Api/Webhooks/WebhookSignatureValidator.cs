using System.Security.Cryptography;
using System.Text;

namespace Rag.NET.Api.Webhooks;

/// <summary>
/// HMAC-SHA256 webhook signature validation over the RAW request body bytes.
/// The provided signature is hex-encoded (either case); a GitHub-style
/// <c>sha256=</c> prefix is tolerated. Comparison is timing-safe.
/// </summary>
internal static class WebhookSignatureValidator
{
    private const string Sha256Prefix = "sha256=";

    internal static bool IsValid(byte[] body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        var signature = signatureHeader.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? signatureHeader[Sha256Prefix.Length..]
            : signatureHeader;

        byte[] provided;
        try
        {
            // FromHexString accepts both hex casings — case-insensitive by construction.
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        // FixedTimeEquals returns false on length mismatch and never short-circuits.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
