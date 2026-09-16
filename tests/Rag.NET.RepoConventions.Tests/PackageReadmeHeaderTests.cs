using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every package README under <c>src/</c> opens with the shared header — the badge
/// row and the "part of Rag.NET" note — and that the badges name the package they ship with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the headers are shared and the bodies are not.</b> A reader arriving on nuget.org for
/// one package should recognise it as the same project the repository shows, which is what the
/// badge row and the note give them. They should not be handed the whole project: the root
/// README is 414 lines about 73 packages, and on the page for <c>Rag.NET.Parsers.Epub</c> that
/// is 72 packages of noise ahead of the install line. So the top of each README converges and
/// the rest stays local, which is also what <c>Directory.Build.props</c> already enforces by
/// refusing to fall back to the root README at pack time.
/// </para>
/// <para>
/// <b>Why a badge is checked for its package id.</b> These headers were inserted into 73 files by
/// one script, and the failure mode of that shape of edit is a copied id: every page showing
/// <c>Rag.NET</c>'s version and download count while claiming to be a different package. Nothing
/// would look broken — <c>img.shields.io</c> answers <c>200</c> and renders a real version — so
/// only an assertion catches it.
/// </para>
/// <para>
/// <b>Links must be absolute.</b> nuget.org renders the README outside the repository, so a
/// relative link that works on GitHub is a 404 there. That is the defect that kept the root
/// README from simply being reused: it carries 22 of them.
/// </para>
/// <para>
/// This guard checks the header. <c>PackageReadmeTests</c> in the package-validation suite checks
/// what actually ships inside each <c>.nupkg</c>, including that every README's C# resolves
/// against its own assembly.
/// </para>
/// </remarks>
public sealed partial class PackageReadmeHeaderTests
{
    /// <summary>
    /// Every published package carries one. Far fewer means the scan lost <c>src/</c> and would
    /// pass by having nothing to check.
    /// </summary>
    private const int FewestPlausibleReadmes = 60;

    private const string BadgeHost = "img.shields.io";

    private const string SharedNote = "https://marcelroozekrans.github.io/Rag.NET/";

    [GeneratedRegex(
        @"img\.shields\.io/nuget/v/(?<package>Rag\.NET[A-Za-z0-9.]*)\.svg",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex NuGetVersionBadge();

    [GeneratedRegex(
        @"\]\((?<target>[^)]+)\)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex MarkdownLink();

    [Fact]
    public void TheScanFindsPlausiblyManyPackageReadmes()
    {
        var readmes = PackageReadmes();

        Assert.True(
            readmes.Count >= FewestPlausibleReadmes,
            $"Found only {readmes.Count} package READMEs under src/, expected at least " +
            $"{FewestPlausibleReadmes}. A guard that finds nothing passes for the wrong reason.");
    }

    [Fact]
    public void EveryPackageReadmeCarriesTheSharedHeader()
    {
        var missing = PackageReadmes()
            .Where(readme => !readme.Text.Contains(BadgeHost, StringComparison.Ordinal)
                          || !readme.Text.Contains(SharedNote, StringComparison.Ordinal))
            .Select(readme => readme.Package)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These packages' READMEs are missing the shared header, so their nuget.org page does " +
            "not read as part of the same project as every other one:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, missing.Select(p => $"  src/{p}/README.md")));
    }

    [Fact]
    public void EveryReadmeBadgeNamesItsOwnPackage()
    {
        var wrong = new List<string>();

        foreach (var readme in PackageReadmes())
        {
            foreach (Match match in NuGetVersionBadge().Matches(readme.Text))
            {
                var badged = match.Groups["package"].Value;
                if (!string.Equals(badged, readme.Package, StringComparison.Ordinal))
                {
                    wrong.Add(
                        $"  src/{readme.Package}/README.md badges '{badged}' — a reader is shown " +
                        "another package's version and downloads");
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            "A NuGet badge names a package other than the one whose README it is in. This renders " +
            "cleanly and is wrong, so nothing but an assertion finds it:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void NoPackageReadmeLinkIsRelative()
    {
        var relative = new List<string>();

        foreach (var readme in PackageReadmes())
        {
            foreach (Match match in MarkdownLink().Matches(readme.Text))
            {
                var target = match.Groups["target"].Value.Trim();

                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith('#'))
                {
                    continue;
                }

                relative.Add($"  src/{readme.Package}/README.md → {target}");
            }
        }

        Assert.True(
            relative.Count == 0,
            "nuget.org renders a package README outside the repository, so these relative links " +
            "are 404s on the page they ship to:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, relative));
    }

    private sealed record Readme(string Package, string Text);

    private static List<Readme> PackageReadmes()
    {
        var source = Path.Combine(TestProject.FindRepositoryRoot(), "src");
        var readmes = new List<Readme>();

        foreach (var directory in Directory.EnumerateDirectories(source, "Rag.NET*"))
        {
            var path = Path.Combine(directory, "README.md");
            if (File.Exists(path))
            {
                readmes.Add(new Readme(Path.GetFileName(directory), File.ReadAllText(path)));
            }
        }

        return readmes;
    }
}
