using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that <c>docs/index.md</c>'s "Pages" table names every page the site publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same defect as the sidebar, on the other navigation surface.</b>
/// <see cref="DocumentationSidebarTests"/> closed the left-hand nav; the landing page keeps its
/// own hand-maintained table of contents, and that table listed 18 of 36 pages — missing RAPTOR,
/// GraphRAG, security, resilience, shadow mode, the whole Reference section, and both pages added
/// in the commit that immediately preceded this guard. A reader who starts at the front page and
/// reads down it is told the documentation is half the size it is.
/// </para>
/// <para>
/// <b>Only one direction is asserted.</b> Every published page must appear; a row pointing at a
/// page that does not exist is already a hard error in the docs build, because
/// <c>onBrokenLinks</c> is <c>throw</c> and these rows are links. That is the opposite of the
/// sidebar, where a dangling id fails the build but a missing one is silent — so each guard
/// covers the half its own surface cannot.
/// </para>
/// <para>
/// <b>What it deliberately does not check.</b> The second column is prose, and prose that
/// overclaims is not detectable here: the row for the architecture page described it as covering
/// "all interfaces and core models" when the page names 14 of Abstractions' 42 and calls its own
/// section "Core interfaces". That was fixed by reading it, and no test will catch the next one.
/// Completeness is mechanical; accuracy is not.
/// </para>
/// </remarks>
public sealed partial class DocumentationIndexTests
{
    private const string IndexRelativePath = "docs/index.md";

    /// <summary>
    /// A table row whose first cell is a markdown link: <c>| [Retrieval](guide/retrieval.md) |</c>.
    /// The link target is what identifies the page; the label is free text.
    /// </summary>
    [GeneratedRegex(
        @"^\|\s*\[[^\]]+\]\((?<target>[^)#]+)(?:#[^)]*)?\)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex LinkedTableRow();

    [Fact]
    public void TheIndexPagesTableNamesEveryPublishedPage()
    {
        var listed = ListedPageIds();

        var missing = PublishedDocumentation.PageIds()
            .Where(id => !string.Equals(id, "index", StringComparison.Ordinal))
            .Where(id => !listed.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These pages are published but are not listed in {IndexRelativePath}'s Pages table, " +
            "so a reader starting at the front page is never told they exist:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, missing.Select(id => $"  docs/{id}")) +
            Environment.NewLine +
            "Add a row for each, or exclude the page in docusaurus.config.ts if it is not meant " +
            "to publish.");
    }

    [Fact]
    public void TheScanFindsPlausiblyManyRows()
    {
        var listed = ListedPageIds();

        Assert.True(
            listed.Count >= 20,
            $"Parsed only {listed.Count} linked rows from {IndexRelativePath}, expected at least " +
            "20. A guard that parses nothing passes for the wrong reason, so this fails instead — " +
            "either the table moved, its rows changed shape, or the parse regressed.");
    }

    /// <summary>
    /// The document ids the Pages table links to, normalised the way Docusaurus ids are: relative
    /// to <c>docs/</c>, extension dropped, forward slashes.
    /// </summary>
    /// <returns>Every page id the table names.</returns>
    private static HashSet<string> ListedPageIds()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), IndexRelativePath);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path))
        {
            var match = LinkedTableRow().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var target = match.Groups["target"].Value.Trim().Replace('\\', '/');

            // index.md sits at the docs root, so its rows are already docs-relative. An absolute
            // or off-site link is not a page reference and is skipped rather than mangled.
            if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith('/'))
            {
                continue;
            }

            var extension = Path.GetExtension(target);
            if (extension.Length > 0)
            {
                target = target[..^extension.Length];
            }

            ids.Add(target);
        }

        return ids;
    }
}
