using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that nothing in <c>src/Rag.NET.DataProviders.Web</c> calls
/// <c>HttpClient.GetStringAsync</c> directly.
/// <para>
/// <b>What this guards against.</b> That method reads the <c>charset</c> from
/// <c>Content-Type</c> and hands it to <c>Encoding.GetEncoding</c>. .NET Core registers only a
/// small set of encodings, so a valid header like <c>text/plain;charset=ISO-8859-2</c> throws
/// <c>InvalidOperationException</c> wrapping <c>ArgumentException</c> — neither of which is an
/// <c>HttpRequestException</c>, so this package's <c>catch (HttpRequestException)</c> blocks never
/// saw it and the exception ended the whole crawl or feed read. Reported against a live site as
/// issue #123, where it had the same call in four places: robots.txt, the page fetch, RSS and
/// sitemap.
/// </para>
/// <para>
/// <b>Why a guard rather than trust.</b> The replacement is an extension method on the same type,
/// so the broken call is one habit away: writing <c>client.GetStringAsync(url, ct)</c> compiles
/// and binds to the instance method, because C# prefers it over any extension. Nothing about that
/// line looks wrong, and the failure only appears against a server declaring an unregistered
/// charset — which no test fixture does by accident. The extension is deliberately named
/// <c>GetStringWithCharsetFallbackAsync</c> so the two cannot be confused at a glance; this test
/// is what makes the naming load-bearing rather than advisory.
/// </para>
/// <para>
/// Scoped to this one package on purpose. Elsewhere <c>GetStringAsync</c> is fine — the hazard is
/// specific to fetching text from arbitrary third-party servers whose headers nobody controls.
/// </para>
/// </summary>
public class WebProviderCharsetGuardTests
{
    private const string GuardedPackage = "src/Rag.NET.DataProviders.Web";
    private const string ForbiddenCall = ".GetStringAsync(";

    /// <summary>
    /// The fewest <c>.cs</c> files this package can plausibly hold. A scan that walks nothing
    /// passes for the wrong reason, so it fails instead.
    /// </summary>
    private const int FewestPlausibleFiles = 4;

    [Fact]
    public void NoWebProviderCallsGetStringAsyncDirectly()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var packageDirectory = Path.Combine(repositoryRoot, GuardedPackage.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            Directory.Exists(packageDirectory),
            $"'{GuardedPackage}' does not exist. If the package moved, move this guard with it — " +
            "silently scanning nothing is how the defect it prevents would return.");

        var scanned = 0;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/", StringComparison.Ordinal)
                || normalised.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(ForbiddenCall, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/')}:{i + 1}");
                }
            }
        }

        Assert.True(
            scanned >= FewestPlausibleFiles,
            $"Only {scanned} .cs files were scanned under '{GuardedPackage}', expected at least " +
            $"{FewestPlausibleFiles}. A guard that scans nothing passes for the wrong reason.");

        Assert.True(
            violations.Count == 0,
            "These call HttpClient.GetStringAsync directly, which throws on a charset this " +
            "runtime cannot resolve and is not caught by catch (HttpRequestException) — issue " +
            "#123. Use the GetStringWithCharsetFallbackAsync extension instead:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", violations));
    }
}
