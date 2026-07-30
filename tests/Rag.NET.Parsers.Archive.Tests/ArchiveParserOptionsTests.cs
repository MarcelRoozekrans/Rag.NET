using System.Globalization;
using Xunit;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// Pins the three bomb bounds: their defaults, that each setter clamps at its ceiling on every
/// construction path, and that asking for more is <b>reported</b> rather than silently corrected.
/// </summary>
/// <remarks>
/// The ceiling assertions name the number as well as the property, and were mutation-checked by
/// deleting the ceiling from the message and confirming each went red. That check is not ceremony: a
/// Phase 3.11 test asserted "the message names the ceiling" and passed against exactly that mutant,
/// because a second incidental occurrence of the literal satisfied the substring. Every requested
/// value below is <c>ceiling + 1</c>, whose digits do not contain the ceiling's, so the "Actual value
/// was …" clause <see cref="ArgumentOutOfRangeException"/> appends cannot stand in for the message.
/// </remarks>
public class ArchiveParserOptionsTests
{
    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultTotalUncompressedBudgetIsTwoHundredFiftySixMegabytes()
    {
        Assert.Equal(256L * 1024 * 1024, new ArchiveParserOptions().MaxTotalUncompressedBytes);
    }

    [Fact]
    public void TheDefaultCompressionRatioIsOneHundred()
    {
        Assert.Equal(100, new ArchiveParserOptions().MaxCompressionRatio);
    }

    [Fact]
    public void TheDefaultEntryCountIsOneThousandTwentyFour()
    {
        Assert.Equal(1024, new ArchiveParserOptions().MaxEntries);
    }

    [Fact]
    public void TheCeilingsAreTwoGigabytesAThousandToOneAndSixtyFiveThousandFiveHundredThirtyFiveEntries()
    {
        // Pinned as literals rather than recomputed from the consts, so a change to a ceiling is a
        // change this test notices instead of one it follows.
        Assert.Equal(2_147_483_648L, ArchiveParserOptions.MaxSupportedTotalUncompressedBytes);
        Assert.Equal(1000, ArchiveParserOptions.MaxSupportedCompressionRatio);
        Assert.Equal(65_535, ArchiveParserOptions.MaxSupportedEntries);
    }

    // ── Clamping ─────────────────────────────────────────────────────────────

    [Fact]
    public void ATotalBudgetAboveTheCeilingIsClamped()
    {
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = ArchiveParserOptions.MaxSupportedTotalUncompressedBytes + 1,
        };

        Assert.Equal(ArchiveParserOptions.MaxSupportedTotalUncompressedBytes, options.MaxTotalUncompressedBytes);
    }

    [Fact]
    public void ACompressionRatioAboveTheCeilingIsClamped()
    {
        var options = new ArchiveParserOptions
        {
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio + 1,
        };

        Assert.Equal(ArchiveParserOptions.MaxSupportedCompressionRatio, options.MaxCompressionRatio);
    }

    [Fact]
    public void AnEntryCountAboveTheCeilingIsClamped()
    {
        var options = new ArchiveParserOptions
        {
            MaxEntries = ArchiveParserOptions.MaxSupportedEntries + 1,
        };

        Assert.Equal(ArchiveParserOptions.MaxSupportedEntries, options.MaxEntries);
    }

    [Fact]
    public void AValueBelowTheCeilingIsKeptExactly()
    {
        // The other direction, so the setters cannot be mutated into "always the ceiling" and pass.
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = 100,
            MaxCompressionRatio = 3,
            MaxEntries = 7,
        };

        Assert.Equal(100, options.MaxTotalUncompressedBytes);
        Assert.Equal(3, options.MaxCompressionRatio);
        Assert.Equal(7, options.MaxEntries);
    }

    [Fact]
    public void ClampingIsRememberedAsARequestSoRegistrationCanStillRefuseIt()
    {
        // The whole point of the Requested… properties: the effective value is safe, and the ask is
        // still visible to whoever should complain about it.
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = ArchiveParserOptions.MaxSupportedTotalUncompressedBytes + 1,
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio + 1,
            MaxEntries = ArchiveParserOptions.MaxSupportedEntries + 1,
        };

        Assert.Equal(
            ArchiveParserOptions.MaxSupportedTotalUncompressedBytes + 1,
            options.RequestedMaxTotalUncompressedBytes);
        Assert.Equal(ArchiveParserOptions.MaxSupportedCompressionRatio + 1, options.RequestedMaxCompressionRatio);
        Assert.Equal(ArchiveParserOptions.MaxSupportedEntries + 1, options.RequestedMaxEntries);
    }

    // ── Exceeding a ceiling is reported ──────────────────────────────────────

    [Fact]
    public void DefaultOptionsAreAccepted()
    {
        // The negative control. Without it every "throws" test below would still pass against a
        // ThrowIfOutOfRange that throws unconditionally.
        new ArchiveParserOptions().ThrowIfOutOfRange("configure");
    }

    [Fact]
    public void ATotalBudgetAboveTheCeilingIsReportedByNameAndNumber()
    {
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = ArchiveParserOptions.MaxSupportedTotalUncompressedBytes + 1,
        };

        AssertReports(
            options,
            nameof(ArchiveParserOptions.MaxTotalUncompressedBytes),
            ArchiveParserOptions.MaxSupportedTotalUncompressedBytes);
    }

    [Fact]
    public void ACompressionRatioAboveTheCeilingIsReportedByNameAndNumber()
    {
        var options = new ArchiveParserOptions
        {
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio + 1,
        };

        AssertReports(
            options,
            nameof(ArchiveParserOptions.MaxCompressionRatio),
            ArchiveParserOptions.MaxSupportedCompressionRatio);
    }

    [Fact]
    public void AnEntryCountAboveTheCeilingIsReportedByNameAndNumber()
    {
        var options = new ArchiveParserOptions
        {
            MaxEntries = ArchiveParserOptions.MaxSupportedEntries + 1,
        };

        AssertReports(options, nameof(ArchiveParserOptions.MaxEntries), ArchiveParserOptions.MaxSupportedEntries);
    }

    [Fact]
    public void ANegativeBoundIsReportedByName()
    {
        var options = new ArchiveParserOptions { MaxEntries = -1 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.ThrowIfOutOfRange("configure"));

        Assert.Contains(nameof(ArchiveParserOptions.MaxEntries), exception.Message, StringComparison.Ordinal);
        Assert.Contains("negative", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts that the refusal names the offending property and the ceiling it exceeded, and blames
    /// the registration parameter.
    /// </summary>
    /// <remarks>
    /// The ceiling is asserted to appear <b>exactly once</b>. A bare <c>Contains</c> is what the
    /// Phase 3.11 test used, and it was satisfiable by an incidental second occurrence of the literal
    /// elsewhere in the message; counting makes such an occurrence a failure to be looked at rather
    /// than a pass to be trusted. The occurrence that matters was then confirmed by deleting it from
    /// the message and watching all three of these tests go red.
    /// </remarks>
    private static void AssertReports(ArchiveParserOptions options, string propertyName, long ceiling)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.ThrowIfOutOfRange("configure"));

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);

        var ceilingText = ceiling.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(1, Occurrences(exception.Message, ceilingText));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
