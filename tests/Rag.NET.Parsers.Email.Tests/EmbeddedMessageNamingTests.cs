using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Pins the names <see cref="EmbeddedMessageMetadata"/> composes for embedded messages, on both
/// sides of the switch from its own private sanitizer to the shared
/// <see cref="FileNameSanitizer"/>: the three behaviours that change, and the composition rules
/// that must not.
/// </summary>
public class EmbeddedMessageNamingTests
{
    private const string NonBreakingSpace = "\u00A0";

    private static DocumentMetadata Parent() => new()
    {
        DocumentId = new DocumentId("naming-1"),
        FileName = "parent.eml",
        ContentType = "message/rfc822",
        Tags = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static string Name(string? subject) =>
        EmbeddedMessageMetadata.Create(Parent(), subject, ".eml", "message/rfc822").FileName;

    // ── What changes ─────────────────────────────────────────────────────────

    [Fact]
    public void ANonBreakingSpaceReExposedByDotTrimmingIsTrimmed()
    {
        // A *bare* trailing non-breaking space was never the defect: the old sanitizer opened
        // with string.Trim(), which is char.IsWhiteSpace-based and removes U+00A0 already. The
        // hole is the closing TrimEnd('.', ' '), which matches exactly two characters in a
        // single pass, so stripping the dot uncovers a non-breaking space it cannot see. The
        // shared sanitizer trims to a fixed point over all whitespace, because trimming dots
        // re-exposes whitespace and vice versa.
        Assert.DoesNotContain('\u00A0', Name("Quarterly report" + NonBreakingSpace + "."));
    }

    [Fact]
    public void AStemLongerThanSixtyFourCharactersSurvivesToOneHundredTwentyEight()
    {
        var subject = new string('a', 100);

        Assert.Equal("parent.eml#" + subject + ".eml", Name(subject));
    }

    [Fact]
    public void AStemLongerThanOneHundredTwentyEightCharactersIsTruncatedThere()
    {
        Assert.Equal("parent.eml#" + new string('a', 128) + ".eml", Name(new string('a', 200)));
    }

    [Fact]
    public void AnAllInvalidStemFallsBackToEmbeddedMessage()
    {
        Assert.Equal("parent.eml#embedded-message.eml", Name("///"));
    }

    // ── What must not change ─────────────────────────────────────────────────

    [Fact]
    public void TheNameIsComposedAsParentHashChild()
    {
        Assert.Equal("parent.eml#child.eml", Name("child"));
    }

    [Fact]
    public void TheSeparatorIsAHash()
    {
        var name = Name("Forwarded Subject");

        Assert.Equal("parent.eml", name[..name.IndexOf('#', StringComparison.Ordinal)]);
        Assert.Equal(1, name.Count(c => c == '#'));
    }

    [Fact]
    public void AnOrdinarySubjectPassesThroughUntouched()
    {
        Assert.Equal("parent.eml#Q3 Results (final).eml", Name("Q3 Results (final)"));
    }

    [Fact]
    public void AnInvalidCharacterIsReplacedWithAnUnderscore()
    {
        Assert.Equal("parent.eml#Re_ status.eml", Name("Re: status"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingSubjectFallsBackToEmbeddedMessage(string? subject)
    {
        Assert.Equal("parent.eml#embedded-message.eml", Name(subject));
    }

    [Fact]
    public void TheEmbeddedMessageKeepsTheParentDocumentId()
    {
        var parent = Parent();

        var metadata = EmbeddedMessageMetadata.Create(parent, "child", ".eml", "message/rfc822");

        Assert.Equal(parent.DocumentId, metadata.DocumentId);
        Assert.Equal("message/rfc822", metadata.ContentType);
    }
}
