using Xunit;

namespace Rag.NET.DataProviders.Tests;

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
    public void Sanitize_OnlyUnderscores_ReturnsFallback()
    {
        // Deliberate consequence of collapsing an all-substituted result: a name that was
        // already nothing but underscores is equally uninformative.
        Assert.Equal("issue-ENG-1", FileNameSanitizer.Sanitize("___", "issue-ENG-1"));
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
    public void Sanitize_IsHostIndependent()
    {
        // The pinned set must cover everything this host considers invalid, so a Linux run
        // (2 invalid chars) can never produce a laxer name than a Windows run (41).
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
