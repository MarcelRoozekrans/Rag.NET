using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Pins the names <see cref="EmbeddedMessageMetadata"/> composes for embedded messages, on both
/// sides of the switch from its own private sanitizer to the shared
/// <see cref="FileNameSanitizer"/>: the four behaviours that change, and the composition rules
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
        Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
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

    /// <summary>
    /// The fourth divergence, found in the Phase 3.6 whole-phase review rather than recorded with
    /// the other three: the two sanitizers apply replacement and trimming in opposite orders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deleted copy called <c>name.Trim()</c> <b>before</b> replacing invalid characters;
    /// <see cref="FileNameSanitizer"/> replaces <b>before</b> trimming. TAB, LF, VT, FF and CR are
    /// C0 control characters <i>and</i> whitespace, so one sitting in a leading or trailing
    /// whitespace run is now turned into <c>_</c> first — and <c>_</c> is not whitespace, so the
    /// trimming pass can no longer remove it.
    /// </para>
    /// <para>
    /// <b>The expectations below are the current, post-3.6 behaviour.</b> The <c>old</c> column
    /// records what the deleted copy produced, so a future reader can tell an intentional change
    /// from a regression: if one of these starts returning the <c>old</c> value again, something
    /// reordered <see cref="FileNameSanitizer.Sanitize"/> and four other call sites moved with it.
    /// </para>
    /// <para>
    /// Reachable in practice through <c>.msg</c>: <c>MsgDocumentParser</c> takes the name from
    /// <c>Storage.Message.Subject</c>, a raw MAPI <c>PidTagSubject</c> with no header
    /// normalization, and MsgReader was observed preserving a trailing tab verbatim.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("report\t", "parent.eml#report_.eml", "parent.eml#report.eml")]
    [InlineData("\treport", "parent.eml#_report.eml", "parent.eml#report.eml")]
    [InlineData("report \t", "parent.eml#report _.eml", "parent.eml#report.eml")]
    [InlineData(".\t", "parent.eml#._.eml", "parent.eml#embedded-message.eml")]
    public void AWhitespaceControlCharacterAtAnEdgeIsReplacedRatherThanTrimmed(
        string subject, string expected, string beforePhase36)
    {
        Assert.Equal(expected, Name(subject));
        Assert.NotEqual(beforePhase36, Name(subject), StringComparer.Ordinal);
    }

    [Fact]
    public void AnOrdinarySpaceAtAnEdgeIsStillTrimmed()
    {
        // The divergence above is specific to whitespace that is *also* invalid. A plain space is
        // untouched by the replacement pass, so trimming still reaches it in either ordering.
        Assert.Equal("parent.eml#report.eml", Name("  report  "));
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
