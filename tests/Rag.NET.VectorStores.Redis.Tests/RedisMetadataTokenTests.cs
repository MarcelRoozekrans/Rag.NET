using Rag.NET.Models;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// The TAG token a filterable metadata value is stored and queried as. No container: this is pure
/// encoding. The RediSearch behaviour it protects against — separator splitting and case folding —
/// is exercised against a real server by the metadata filter tests.
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

    /// <summary>Two values differing only in case must not collide.</summary>
    [Fact]
    public void CaseIsSignificant()
    {
        Assert.NotEqual(
            RedisVectorStore.MetadataToken((MetadataValue)"ACME"),
            RedisVectorStore.MetadataToken((MetadataValue)"acme"),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Every kind round-trips through one canonical textual form — <c>MetadataValue.ToString()</c>
    /// — so the write path and the filter path cannot drift.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with spaces and : colons")]
    [InlineData("")]
    public void AStringTokenIsStableForTheSameValue(string value)
    {
        Assert.Equal(
            RedisVectorStore.MetadataToken((MetadataValue)value),
            RedisVectorStore.MetadataToken((MetadataValue)value));
    }

    /// <summary>The field name is namespaced so a metadata key named <c>text</c> is representable.</summary>
    [Fact]
    public void TheFieldNameIsNamespaced()
    {
        Assert.Equal("md_text", RedisVectorStore.MetadataFieldName("text"));
    }
}
