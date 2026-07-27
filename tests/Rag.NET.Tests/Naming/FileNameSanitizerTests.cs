using Xunit;

namespace Rag.NET.Tests.Naming;

public sealed class FileNameSanitizerTests
{
    [Fact]
    public void Sanitize_InvalidChars_ReplacedWithUnderscore()
    {
        Assert.Equal("a_b_c_d", FileNameSanitizer.Sanitize("a/b:c*d", "fallback"));
    }

    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData(":")]
    [InlineData("\"")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("|")]
    [InlineData("?")]
    [InlineData("*")]
    public void Sanitize_EachPinnedPunctuationChar_Replaced(string invalid)
    {
        Assert.Equal("a_b", FileNameSanitizer.Sanitize($"a{invalid}b", "fallback"));
    }

    [Fact]
    public void Sanitize_ControlChars_Replaced()
    {
        Assert.Equal("a_b", FileNameSanitizer.Sanitize("a\u0001b", "fallback"));
    }

    [Fact]
    public void Sanitize_EveryC0ControlChar_Replaced()
    {
        for (int i = 0; i <= 0x1F; i++)
        {
            var input = string.Concat("a", ((char)i).ToString(), "b");
            Assert.Equal("a_b", FileNameSanitizer.Sanitize(input, "fallback"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Sanitize_NullOrWhitespace_ReturnsFallback(string? value)
    {
        Assert.Equal("fallback", FileNameSanitizer.Sanitize(value, "fallback"));
    }

    [Fact]
    public void Sanitize_AllInvalid_ReturnsFallback()
    {
        Assert.Equal("message-42", FileNameSanitizer.Sanitize("///", "message-42"));
    }

    [Fact]
    public void Sanitize_OnlyUnderscores_Unchanged()
    {
        // Only underscores the sanitizer *introduced* count as "nothing survived"; a title the
        // user really typed as "___" is kept, exactly as "a__" is.
        Assert.Equal("___", FileNameSanitizer.Sanitize("___", "issue-ENG-1"));
    }

    [Theory]
    [InlineData("name. . .")]
    [InlineData("a. . . . .")]
    [InlineData("Report </ . .")]
    [InlineData(" . x . . ")]
    public void Sanitize_AlternatingDotsAndSpaces_LeavesNeitherTrailing(string value)
    {
        // Regression: trimming dots re-exposes whitespace and vice versa, so the trailing pass
        // has to run to a fixed point. A single pass of each left "name. " with a trailing
        // space, violating the documented return contract.
        var result = FileNameSanitizer.Sanitize(value, "fallback");

        Assert.False(char.IsWhiteSpace(result[^1]), $"'{result}' ends in whitespace");
        Assert.NotEqual('.', result[^1]);
    }

    [Fact]
    public void Sanitize_AlternatingSuffix_TrimsToStem()
    {
        Assert.Equal("name", FileNameSanitizer.Sanitize("name. . .", "fallback"));
        Assert.Equal("a", FileNameSanitizer.Sanitize("a. . . . .", "fallback"));
        Assert.Equal("Report __", FileNameSanitizer.Sanitize("Report </ . .", "fallback"));
    }

    [Fact]
    public void Sanitize_TruncationSplittingSurrogatePair_DropsTheLoneHighSurrogate()
    {
        // "ab" + an astral-plane emoji; a 3-char cap would split the pair.
        var result = FileNameSanitizer.Sanitize("ab\U0001F600cd", "fallback", maxLength: 3);

        Assert.Equal("ab", result);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }

    [Fact]
    public void Sanitize_TruncationNotSplittingSurrogatePair_KeepsIt()
    {
        var result = FileNameSanitizer.Sanitize("ab\U0001F600cd", "fallback", maxLength: 4);

        Assert.Equal("ab\U0001F600", result);
    }

    [Fact]
    public void Sanitize_TruncationExposingTrailingDot_TrimsIt()
    {
        Assert.Equal("abc", FileNameSanitizer.Sanitize("abc. def", "fallback", maxLength: 4));
    }

    [Fact]
    public void Sanitize_OnlyDots_ReturnsFallback()
    {
        Assert.Equal("page-7", FileNameSanitizer.Sanitize("...", "page-7"));
    }

    [Fact]
    public void Sanitize_TrimsWhitespaceAndTrailingDots()
    {
        Assert.Equal("name", FileNameSanitizer.Sanitize("  name.  ", "fallback"));
    }

    [Fact]
    public void Sanitize_LeadingDots_Preserved()
    {
        // Only *trailing* dots are invalid on Windows; a leading dot is a legal name.
        Assert.Equal(".gitignore", FileNameSanitizer.Sanitize(".gitignore", "fallback"));
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 500), "fallback");

        Assert.Equal(128, result.Length);
        Assert.Equal(new string('a', 128), result);
    }

    [Fact]
    public void Sanitize_TruncatesToExplicitMaxLength()
    {
        Assert.Equal("abcde", FileNameSanitizer.Sanitize("abcdefghij", "fallback", maxLength: 5));
    }

    [Fact]
    public void Sanitize_FallbackIsAlsoSanitized()
    {
        Assert.Equal("bad_name", FileNameSanitizer.Sanitize(null, "bad/name"));
    }

    [Fact]
    public void Sanitize_FallbackIsAlsoTruncated()
    {
        var result = FileNameSanitizer.Sanitize(null, new string('f', 500), maxLength: 10);

        Assert.Equal(new string('f', 10), result);
    }

    [Fact]
    public void Sanitize_CoversEveryCharThisHostConsidersInvalid()
    {
        // Asserts the pinned set is a superset of *this host's* invalid set, so the sanitizer
        // is never laxer than the platform. Note this is a weak check off Windows: it covers
        // 41 characters here and only 2 on Linux CI, which is precisely the host-dependence
        // being designed out. The unconditional coverage of the pinned set lives in
        // Sanitize_EachPinnedPunctuationChar_Replaced and Sanitize_EveryC0ControlChar_Replaced,
        // which assert the same characters on every platform.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            var input  = string.Concat("a", c.ToString(), "b");
            var actual = FileNameSanitizer.Sanitize(input, "fallback");

            Assert.Equal("a_b", actual);
        }
    }

    [Fact]
    public void Sanitize_ValidName_Unchanged()
    {
        Assert.Equal("Invoice Q1-2026", FileNameSanitizer.Sanitize("Invoice Q1-2026", "fallback"));
    }

    [Fact]
    public void Sanitize_ValidNameWithInnerDots_Unchanged()
    {
        Assert.Equal("v1.2.3 release", FileNameSanitizer.Sanitize("v1.2.3 release", "fallback"));
    }

    [Fact]
    public void Sanitize_ReservedDeviceName_DeliberatelyUnchanged()
    {
        // Documented non-goal: these names are metadata, never filesystem paths.
        Assert.Equal("CON", FileNameSanitizer.Sanitize("CON", "fallback"));
        Assert.Equal("NUL", FileNameSanitizer.Sanitize("NUL", "fallback"));
    }

    [Fact]
    public void Sanitize_UnsalvageableFallback_ReturnsLastResort()
    {
        Assert.Equal("untitled", FileNameSanitizer.Sanitize("///", "???"));
    }

    [Fact]
    public void Sanitize_NullFallback_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => FileNameSanitizer.Sanitize("value", null!));

        Assert.Equal("fallback", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sanitize_NonPositiveMaxLength_Throws(int maxLength)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => FileNameSanitizer.Sanitize("value", "fallback", maxLength));

        Assert.Equal("maxLength", ex.ParamName);
    }
}
