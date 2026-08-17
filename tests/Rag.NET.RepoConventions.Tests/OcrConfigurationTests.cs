using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Keeps OCR language and tessdata configurable, and keeps the two parsers that do OCR agreeing.
/// </summary>
/// <remarks>
/// <para>
/// <b>#299.</b> <c>ImageDocumentParser.TryOcr</c> constructed Tesseract with two literals —
/// <c>@"./tessdata"</c> and <c>"eng"</c> — while <c>PdfParserOptions</c> had exposed both as
/// settings all along. Anyone ingesting a German document through both parsers could configure one
/// and not the other, and nothing said so.
/// </para>
/// <para>
/// Here rather than in either parser's own tests, for two reasons. The parity check would otherwise
/// force <c>Rag.NET.Parsers.Vision.Tests</c> to reference <c>Rag.NET.Parsers.Pdf</c> and its native
/// OCR dependencies in order to compare two strings. And the literal check below is a rule about the
/// repository, not about one package — a third OCR site would be subject to it without anyone
/// remembering to add a test.
/// </para>
/// </remarks>
public sealed partial class OcrConfigurationTests
{
    [Fact]
    public void TheTwoOcrParsersAgreeOnTheirDefaults()
    {
        var vision = ReadDefaults("src/Rag.NET.Parsers.Vision/ImageDescriptionOptions.cs");
        var pdf = ReadDefaults("src/Rag.NET.Parsers.Pdf/PdfParserOptions.cs");

        foreach (var setting in new[] { "OcrLanguage", "TessDataPath" })
        {
            Assert.True(
                vision.TryGetValue(setting, out var visionValue),
                $"Rag.NET.Parsers.Vision does not expose {setting}. It was a literal in TryOcr until " +
                "#299, and the PDF parser has had it as a setting all along.");
            Assert.True(pdf.TryGetValue(setting, out var pdfValue), $"PdfParserOptions lost {setting}.");

            Assert.True(
                string.Equals(visionValue, pdfValue, StringComparison.Ordinal),
                $"The two OCR parsers disagree on {setting}: Vision defaults to {visionValue}, " +
                $"PDF to {pdfValue}. Same job, same document, so a reader who learns one should have " +
                "learned the other.");
        }
    }

    /// <remarks>
    /// <para>
    /// Catches the shape of the original defect rather than its instance: a Tesseract engine built
    /// from string literals instead of from settings. A future third OCR site is covered without
    /// anyone remembering this test exists.
    /// </para>
    /// <para>
    /// Matches on the constructor call specifically, not on the presence of <c>"eng"</c> anywhere —
    /// the string is a perfectly good <i>default</i> on an options property, which is exactly where
    /// both parsers now keep it.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoTesseractEngineIsConstructedFromLiterals()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(TestProject.FindRepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (HardcodedTesseractEngine().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These construct a Tesseract engine from string literals rather than from options: " +
            $"{string.Join(", ", offenders)}. That is how the Vision parser silently read English " +
            "only (#299) while the PDF parser had been configurable for months.");
    }

    /// <summary>Reads <c>public string X { get; set; } = "value";</c> declarations from a file.</summary>
    /// <param name="relativePath">Path from the repository root.</param>
    /// <returns>Property name to declared default.</returns>
    private static Dictionary<string, string> ReadDefaults(string relativePath)
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), relativePath);
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in StringPropertyDefault().Matches(File.ReadAllText(path)))
        {
            defaults[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return defaults;
    }

    /// <summary>Matches a public string property with a literal default.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"public\s+string\s+(?<name>\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex StringPropertyDefault();

    /// <summary>
    /// Matches a <c>TesseractEngine</c> constructed with a string literal in its first two arguments.
    /// </summary>
    /// <remarks>
    /// The escapes keep this pattern from matching its own source, so the conventions project does
    /// not report itself — the same trap <c>TestProject</c> documents three times over.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"new\s+(?:Tesseract\.)?TesseractEngine\(\s*@?""",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex HardcodedTesseractEngine();
}
