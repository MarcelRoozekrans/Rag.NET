using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that <c>Rag.NET.DataProviders.Web</c> still bans <c>HttpClient.GetStringAsync</c>
/// through <c>BannedApiAnalyzers</c>.
/// <para>
/// <b>What the ban guards against.</b> That method reads the <c>charset</c> from
/// <c>Content-Type</c> and hands it to <c>Encoding.GetEncoding</c>. .NET Core registers only a
/// small set of encodings, so a valid header like <c>text/plain;charset=ISO-8859-2</c> throws
/// <c>InvalidOperationException</c> wrapping <c>ArgumentException</c> — neither of which is an
/// <c>HttpRequestException</c>, so this package's <c>catch (HttpRequestException)</c> blocks never
/// saw it and the exception ended the whole crawl or feed read. Reported against a live site as
/// issue #123, where the same call appeared four times: robots.txt, the page fetch, RSS and
/// sitemap.
/// </para>
/// <para>
/// <b>Why this test exists alongside the analyzer.</b> The analyzer is the better enforcement —
/// it fails the build at the call site and shows a squiggle in the editor, which a test run
/// cannot. But it only bans what <c>BannedSymbols.txt</c> lists, and it only reads that file
/// because the csproj declares it as <c>AdditionalFiles</c>. Delete either and the analyzer keeps
/// running, reports nothing, and the build stays green — an inert guard that looks exactly like a
/// working one. This test is what makes deleting them fail loudly instead.
/// </para>
/// <para>
/// It deliberately checks the wiring rather than re-scanning the source for the call: doing that
/// would duplicate the analyzer's job less well, and would keep passing after someone unwired it.
/// Suggested by StefH in review of #126, which originally shipped the source scan.
/// </para>
/// </summary>
public class WebProviderCharsetGuardTests
{
    private const string ProjectFile =
        "src/Rag.NET.DataProviders.Web/Rag.NET.DataProviders.Web.csproj";

    private const string BannedSymbolsFile =
        "src/Rag.NET.DataProviders.Web/BannedSymbols.txt";

    [Fact]
    public void TheWebProviderProjectStillWiresTheBannedApiAnalyzer()
    {
        var project = ReadRepositoryFile(ProjectFile);

        Assert.True(
            project.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", StringComparison.Ordinal),
            $"'{ProjectFile}' no longer references BannedApiAnalyzers, so nothing stops this " +
            "package calling HttpClient.GetStringAsync again — see issue #123.");

        Assert.True(
            project.Contains("BannedSymbols.txt", StringComparison.Ordinal)
            && project.Contains("AdditionalFiles", StringComparison.Ordinal),
            $"'{ProjectFile}' no longer declares BannedSymbols.txt as AdditionalFiles. The " +
            "analyzer only reads the file through that declaration, so without it the package " +
            "compiles clean while banning nothing.");
    }

    [Fact]
    public void EveryGetStringAsyncOverloadIsStillBanned()
    {
        var banned = ReadRepositoryFile(BannedSymbolsFile);

        // All four overloads, because banning only the one currently called leaves the other
        // three as a silent way back to the same defect.
        string[] overloads =
        [
            "M:System.Net.Http.HttpClient.GetStringAsync(System.String)",
            "M:System.Net.Http.HttpClient.GetStringAsync(System.Uri)",
            "M:System.Net.Http.HttpClient.GetStringAsync(System.String,System.Threading.CancellationToken)",
            "M:System.Net.Http.HttpClient.GetStringAsync(System.Uri,System.Threading.CancellationToken)",
        ];

        var missing = overloads
            .Where(overload => !banned.Contains(overload, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"'{BannedSymbolsFile}' no longer bans these overloads, each of which reaches the " +
            "same charset defect (issue #123):" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", missing));
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(path),
            $"'{relativePath}' does not exist. If it moved, move this guard with it — a guard " +
            "that cannot find what it checks is not protecting anything.");

        return File.ReadAllText(path);
    }
}
