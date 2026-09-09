using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// The TAG token a filterable metadata value is stored and queried as. No container: this is pure
/// encoding. The RediSearch behaviour the encoding protects against — separator splitting — is
/// exercised against a real server by the metadata filter tests; case folding is a schema flag
/// (<c>CASESENSITIVE</c>), not something this encoding controls, and is pinned there too.
/// </summary>
public sealed class RedisMetadataTokenTests
{
    /// <summary>
    /// The kind prefix is what stops a filter of the string "3" matching the number 3, which
    /// <c>SearchOptions.MetadataFilter</c> requires in as many words.
    /// </summary>
    [Fact]
    public void ANumberAndAStringOfTheSameTextProduceDifferentTokens()
    {
        Assert.NotEqual(
            RedisVectorStore.MetadataToken((MetadataValue)3d),
            RedisVectorStore.MetadataToken((MetadataValue)"3"),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A TAG field splits its value on <c>,</c>, so a comma inside a value would store as two tags
    /// and match neither. Base64Url's alphabet contains no comma, which is why the value is encoded
    /// rather than escaped — the same argument 6.2.30 made for the Azure key.
    /// </summary>
    [Fact]
    public void AValueContainingACommaEncodesWithoutOne()
    {
        var token = RedisVectorStore.MetadataToken((MetadataValue)"acme, inc");

        Assert.DoesNotContain(',', token);
    }

    /// <summary>
    /// A string token carries the string kind prefix and decodes to the original value, verifying
    /// that <c>MetadataValue.ToString()</c> round-trips through Base64Url encoding.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with spaces and : colons")]
    [InlineData("")]
    public void AStringTokenCarriesItsKindAndDecodesBackToItsValue(string value)
    {
        var token = RedisVectorStore.MetadataToken((MetadataValue)value);

        Assert.True(token.StartsWith("s:", StringComparison.Ordinal), $"Token should start with 's:' but was '{token}'");

        var colonIndex = token.IndexOf(':');
        var encodedPart = token.AsSpan(colonIndex + 1);

        // Decode the Base64Url part
        var buffer = new byte[Base64Url.GetMaxDecodedLength(encodedPart.Length)];
        var status = Base64Url.DecodeFromChars(encodedPart, buffer, out _, out var bytesDecoded, isFinalBlock: true);

        Assert.True(status == OperationStatus.Done, $"Base64Url decode failed with status {status}");

        var decodedText = Encoding.UTF8.GetString(buffer, 0, bytesDecoded);
        Assert.Equal(value, decodedText);
    }

    /// <summary>The field name is namespaced so a metadata key named <c>text</c> is representable.</summary>
    [Fact]
    public void TheFieldNameIsNamespaced()
    {
        Assert.Equal("md_text", RedisVectorStore.MetadataFieldName("text"));
    }
}
