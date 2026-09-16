using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every page Docusaurus publishes is reachable from <c>sidebars.ts</c>, and that
/// every id the sidebar names has a file behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a test rather than the docs build.</b> Docusaurus generates a route for every file under
/// <c>docs/</c> whether or not a sidebar names it. A page left out is built, deployed and
/// reachable by URL, while being invisible to anyone browsing — and <c>npm run build</c> is
/// perfectly happy, because nothing about the page is broken. The docs workflow going green is
/// therefore not evidence the nav is complete, and never was: thirteen pages sat in exactly that
/// state, 4,751 of the site's 17,500 lines, including the whole of RAPTOR, GraphRAG, security,
/// resilience, conversational memory and every SaaS connector. Two of them — <c>guide/raptor</c>
/// and <c>guide/graphrag</c> — had no inbound link from any listed page either, so browsing could
/// not reach them by any route at all.
/// </para>
/// <para>
/// <b>Both directions, for different failure modes.</b> An unlisted page is silent: it publishes
/// and nobody finds it. A listed id with no file is loud — Docusaurus fails the build — so that
/// half is not here to catch a shipped defect but to name the cause at the place that explains it,
/// in a suite that runs on every push rather than only when the path filter fires
/// (<c>docs.yml</c> builds the site solely for changes under <c>docs/</c> and the site config, so
/// renaming a page from a <c>src/</c>-only branch misses it).
/// </para>
/// <para>
/// <b>The exclusions come from the config, not from here.</b> <c>docusaurus.config.ts</c> already
/// declares which trees are not published — <c>plans/</c>, <c>planning/</c> and the
/// <c>pre-push-review-*</c> artefacts — and a second copy of that list in a test is a thing to
/// forget. This parses the <c>exclude</c> array out of the config so that adding an excluded tree
/// stays one edit, and fails if it cannot find the array rather than silently scanning everything
/// and demanding 300 plan documents be added to the nav.
/// </para>
/// </remarks>
public sealed partial class DocumentationSidebarTests
{
    /// <summary>
    /// The site carried 34 published pages when this guard was written. A scan that finds far fewer
    /// has lost the tree or stopped recognising markdown, and would then pass by having nothing to
    /// check — the same way a regex that stops matching declarations passes for the wrong reason.
    /// </summary>
    private const int FewestPlausiblePublishedPages = 25;

    [GeneratedRegex(@"'(?<value>[^']*)'", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SingleQuotedString();

    /// <summary>
    /// Sidebar entries are quoted document ids — <c>'guide/raptor'</c> — alongside quoted values
    /// for <c>type</c> and <c>label</c>. Only ids resolve to a file, so the structural keywords are
    /// filtered by their key rather than by guessing at the shape of the value.
    /// </summary>
    [GeneratedRegex(
        @"(?:type|label):\s*'(?<value>[^']*)'",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex KeyedString();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*", RegexOptions.NonBacktracking)]
    private static partial Regex LineComment();

    /// <summary>
    /// An <c>import ... from '@docusaurus/…'</c> module specifier is a quoted string that is not a
    /// document id, and reads as a dangling sidebar entry unless it is removed first.
    /// </summary>
    [GeneratedRegex(@"^\s*import[^\r\n]*", RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex ImportStatement();

    [Fact]
    public void TheScanFindsPlausiblyManyPublishedPages()
    {
        var pages = PublishedDocumentation.PageIds();

        Assert.True(
            pages.Count >= FewestPlausiblePublishedPages,
            $"Found only {pages.Count} published pages under docs/, expected at least " +
            $"{FewestPlausiblePublishedPages}. A guard that stops finding pages passes because it " +
            "has nothing left to check, so this fails instead.");
    }

    [Fact]
    public void EveryPublishedPageIsReachableFromTheSidebar()
    {
        var listed = SidebarDocumentIds();

        var unreachable = PublishedDocumentation.PageIds()
            .Where(id => !listed.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unreachable.Count == 0,
            "These pages are published but named by no sidebar entry, so they deploy to a live " +
            "URL that nothing in the navigation links to:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, unreachable.Select(id => $"  docs/{id}")) +
            Environment.NewLine +
            "Add each to sidebars.ts, or add its directory to the `exclude` list in " +
            "docusaurus.config.ts if it is not meant to publish.");
    }

    [Fact]
    public void EverySidebarEntryNamesAPageThatExists()
    {
        var published = PublishedDocumentation.PageIds();

        var dangling = SidebarDocumentIds()
            .Where(id => !published.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dangling.Count == 0,
            "These sidebar entries name a document id with no file behind it, which fails the " +
            "docs build:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, dangling.Select(id => $"  '{id}'")));
    }

    /// <summary>
    /// The quoted document ids in <c>sidebars.ts</c>, with the quoted values of <c>type</c> and
    /// <c>label</c> removed — those are category structure, not pages.
    /// </summary>
    /// <remarks>
    /// Comments come out first. The file is heavily commented prose, and an apostrophe in it
    /// ("the site's own") reads as an opening quote to any regex that pairs <c>'</c> with <c>'</c>
    /// — the first draft of this guard reported every page as unreachable and every stray prose
    /// fragment as a dangling entry, which is a false failure in both directions at once. The
    /// stripper is deliberately naive: it would also cut a <c>//</c> inside a string literal, which
    /// a sidebar id cannot contain.
    /// </remarks>
    /// <returns>Every document id the sidebar names.</returns>
    private static HashSet<string> SidebarDocumentIds()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "sidebars.ts");
        var source = File.ReadAllText(path);
        var text = ImportStatement().Replace(
            LineComment().Replace(BlockComment().Replace(source, " "), " "),
            " ");

        var structural = KeyedString().Matches(text)
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

        return SingleQuotedString().Matches(text)
            .Select(match => match.Groups["value"].Value)
            .Where(value => !structural.Contains(value))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
