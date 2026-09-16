using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Which pages under <c>docs/</c> the documentation site actually publishes, read from
/// <c>docusaurus.config.ts</c>'s own <c>exclude</c> list rather than from a copy of it.
/// </summary>
/// <remarks>
/// <para>
/// Three guards need this same answer — <see cref="DocumentationSidebarTests"/>,
/// <see cref="DocumentationIndexTests"/> and <see cref="DocumentationPackageReferenceTests"/> —
/// and they must not be able to disagree about it. The package guard originally named the
/// excluded directories itself, with a comment arguing the set was "small and stable enough" to
/// restate. That held for about a week: excluding <c>reference/features.md</c> added a fourth
/// entry of a shape the hardcoded list had no way to express, and a guard that scans a page the
/// site does not publish is reporting on text nobody reads.
/// </para>
/// <para>
/// Parsing TypeScript with a regex is not something to do casually, but the alternative here is
/// worse: the config is the only place the exclusion list is authoritative, and the failure mode
/// of not reading it is silent — a guard keeps passing while measuring the wrong set. The parse
/// fails loudly instead of degrading, so a config that stops matching this shape stops the suite
/// rather than quietly widening the scan to three hundred plan documents.
/// </para>
/// </remarks>
public static partial class PublishedDocumentation
{
    [GeneratedRegex(
        @"exclude:\s*\[(?<entries>[^\]]*)\]",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ExcludeArray();

    [GeneratedRegex(@"'(?<value>[^']*)'", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SingleQuotedString();

    private static bool IsMarkdown(string extension) =>
        extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Document ids as Docusaurus derives them: the path under <c>docs/</c> without its
    /// extension, with forward slashes on every platform.
    /// </summary>
    /// <returns>The id of every page the site publishes.</returns>
    public static ISet<string> PageIds()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var documentationRoot = Path.Combine(repositoryRoot, "docs");
        var excluded = ExcludedGlobs(repositoryRoot);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(documentationRoot, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (!IsMarkdown(extension))
            {
                continue;
            }

            var relative = Path.GetRelativePath(documentationRoot, path).Replace('\\', '/');

            if (IsExcluded(relative, excluded))
            {
                continue;
            }

            ids.Add(relative[..^extension.Length]);
        }

        return ids;
    }

    /// <summary>Whether the site's <c>exclude</c> list keeps this page out of the build.</summary>
    /// <param name="relativePath">The page's path relative to <c>docs/</c>, forward-slashed.</param>
    /// <param name="excluded">The globs from the config.</param>
    /// <returns>Whether the page is unpublished.</returns>
    public static bool IsExcluded(string relativePath, IReadOnlyList<string> excluded)
    {
        foreach (var glob in excluded)
        {
            if (MatchesGlob(relativePath, glob))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the <c>exclude</c> array out of the Docusaurus config.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>Each excluded glob, as written in the config.</returns>
    public static IReadOnlyList<string> ExcludedGlobs(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "docusaurus.config.ts");
        var match = ExcludeArray().Match(File.ReadAllText(path));

        Assert.True(
            match.Success,
            $"Could not find an `exclude:` array in {path}. These guards read the site's own " +
            "exclusion list rather than duplicating it; if the option moved or was renamed, this " +
            "fails rather than scanning every plan document and demanding it be added to the nav.");

        return SingleQuotedString().Matches(match.Groups["entries"].Value)
            .Select(entry => entry.Groups["value"].Value)
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Matches the three glob shapes the config uses — a directory prefix (<c>plans/**</c>), a
    /// filename prefix (<c>pre-push-review-*.md</c>) and an exact path
    /// (<c>reference/features.md</c>) — rather than implementing globbing.
    /// </summary>
    /// <param name="relativePath">The page's path relative to <c>docs/</c>.</param>
    /// <param name="glob">One entry from the config's <c>exclude</c> array.</param>
    /// <returns>Whether the glob covers this page.</returns>
    private static bool MatchesGlob(string relativePath, string glob)
    {
        if (glob.EndsWith("/**", StringComparison.Ordinal))
        {
            return relativePath.StartsWith(glob[..^2], StringComparison.Ordinal);
        }

        var star = glob.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return relativePath.Equals(glob, StringComparison.Ordinal);
        }

        var prefix = glob[..star];
        var suffix = glob[(star + 1)..];

        return relativePath.StartsWith(prefix, StringComparison.Ordinal)
            && relativePath.EndsWith(suffix, StringComparison.Ordinal);
    }
}
