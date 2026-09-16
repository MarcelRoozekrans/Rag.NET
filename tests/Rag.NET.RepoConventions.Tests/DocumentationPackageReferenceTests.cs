using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every package id the published documentation names <b>as a package</b> exists
/// under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists for.</b> The package decomposition retired eight ids, and four
/// published pages went on naming them: <c>Rag.NET.Parsers.Word</c>, <c>.Excel</c> and
/// <c>.PowerPoint</c> (merged into <c>.Office</c>), <c>Rag.NET.Chunking.Semantic</c> and
/// <c>.TokenAware</c> (merged into <c>Rag.NET.Chunking</c>), and the four standalone Graph
/// connectors (merged into <c>.Microsoft365</c>). The landing page's catalogue carried eight such
/// rows, so the first table a reader met listed packages that <c>dotnet add package</c> cannot
/// resolve. The <c>ROADMAP</c> recorded it in 2026-08-04 and it survived every subsequent docs
/// build, because a wrong package name is not something a site build can see.
/// </para>
/// <para>
/// <b>Why position and not spelling.</b> The obvious guard — "every <c>Rag.NET.*</c> token in the
/// docs must be a directory under <c>src/</c>, or a namespace" — catches none of the eight. The
/// namespaces outlived the packages: the type inside <c>Rag.NET.Parsers.Office</c> is still
/// declared in <c>namespace Rag.NET.Parsers.Word</c>, so every retired id is still a real
/// namespace and a name-based check waves all eight through. Docs legitimately name namespaces and
/// test projects too, so the token alone can never decide.
/// </para>
/// <para>
/// <b>What it checks instead.</b> Three positions where a token can only mean a package:
/// </para>
/// <list type="bullet">
/// <item><description>a <c>dotnet add package</c> or <c>dotnet tool install</c> argument;</description></item>
/// <item><description>a markdown table cell under a header naming a package column — "NuGet
/// package", "Package", "You install" — which is the shape all four defective pages used;</description></item>
/// <item><description>an oss-libraries <c>**Used in:**</c> line, that page's convention for
/// attributing a dependency to the packages that consume it.</description></item>
/// </list>
/// <para>
/// Prose stays unguarded, deliberately. A sentence naming <c>Rag.NET.Abstractions</c> is usually
/// talking about the namespace, and failing on it would push writers towards saying less rather
/// than towards saying it correctly.
/// </para>
/// </remarks>
public sealed partial class DocumentationPackageReferenceTests
{
    /// <summary>
    /// The published pages carried 38 install lines and roughly 110 package-column cells when this
    /// guard was written. A scan finding a handful has lost the tables, and would then pass by
    /// having nothing to check.
    /// </summary>
    private const int FewestPlausibleCitations = 60;

    private static readonly string[] ExcludedDirectories = ["plans", "planning"];

    private const string ExcludedFilePrefix = "pre-push-review-";

    [GeneratedRegex(
        @"dotnet\s+(?:add\s+package|tool\s+install)\s+(?<package>Rag\.NET[A-Za-z0-9.]*)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex InstallCommand();

    /// <summary>
    /// A cell holding a backticked package id and nothing else. A cell that says "`Rag.NET.Chunking`
    /// or one of its siblings" is prose in a table and is left alone.
    /// </summary>
    [GeneratedRegex(
        @"^`(?<package>Rag\.NET[A-Za-z0-9.]*)`$",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex BareBacktickedPackage();

    [GeneratedRegex(
        @"^\*\*Used in:\*\*\s*(?<rest>.*)$",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex UsedInLine();

    [GeneratedRegex(
        @"`(?<package>Rag\.NET[A-Za-z0-9.]*)`",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex BacktickedPackage();

    /// <summary>
    /// Header cells whose column holds package ids. Matched on the whole normalised cell so that a
    /// "Package" column is found and a "Packages affected by X" column is not.
    /// </summary>
    private static readonly HashSet<string> PackageColumnHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "package", "nuget package", "you install" };

    [Fact]
    public void TheScanFindsPlausiblyManyPackageCitations()
    {
        var citations = ParseCitations();

        Assert.True(
            citations.Count >= FewestPlausibleCitations,
            $"Found only {citations.Count} package citations across the published docs, expected " +
            $"at least {FewestPlausibleCitations}. A guard that parses nothing passes for the " +
            "wrong reason, so this fails instead — either the pages moved, the tables changed " +
            "shape, or the parse regressed.");
    }

    [Fact]
    public void EveryPackageTheDocsNameAsAPackageExistsUnderSrc()
    {
        var sourceDirectory = Path.Combine(TestProject.FindRepositoryRoot(), "src");

        var missing = ParseCitations()
            .Where(citation => !Directory.Exists(Path.Combine(sourceDirectory, citation.Package)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These pages name a package that does not exist under src/, in a position that can " +
            "only mean a package — an install command, a package-column table cell, or a " +
            "'Used in:' attribution. A reader following any of them gets a package-not-found:" +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                missing.Select(c => $"  {c.RelativePath}:{c.LineNumber}: '{c.Package}' ({c.Position})")) +
            Environment.NewLine +
            "Fix the page. Namespaces outlive the packages they were split out of, so a retired " +
            "id still resolves as a namespace and still reads as plausible — check src/, not " +
            "your memory of the name.");
    }

    private sealed record Citation(string Package, string RelativePath, int LineNumber, string Position);

    private static List<Citation> ParseCitations()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var documentationRoot = Path.Combine(repositoryRoot, "docs");
        var citations = new List<Citation>();

        foreach (var path in Directory.EnumerateFiles(documentationRoot, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(documentationRoot, path).Replace('\\', '/');

            if (IsExcludedFromPublication(relative))
            {
                continue;
            }

            CollectFromFile(File.ReadAllLines(path), $"docs/{relative}", citations);
        }

        return citations;
    }

    /// <summary>
    /// Mirrors <c>docusaurus.config.ts</c>'s <c>exclude</c>: the internal planning trees and the
    /// pre-push-review artefacts are not published, so a stale name in one is not a reader-facing
    /// defect. <see cref="DocumentationSidebarTests"/> reads the config directly; here the set is
    /// small and stable enough that naming it is clearer than parsing TypeScript twice.
    /// </summary>
    /// <param name="relativePath">The page's path relative to <c>docs/</c>.</param>
    /// <returns>Whether the page is kept out of the built site.</returns>
    private static bool IsExcludedFromPublication(string relativePath)
    {
        foreach (var directory in ExcludedDirectories)
        {
            if (relativePath.StartsWith($"{directory}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return Path.GetFileName(relativePath).StartsWith(ExcludedFilePrefix, StringComparison.Ordinal);
    }

    private static void CollectFromFile(string[] lines, string relativePath, List<Citation> citations)
    {
        // Rebuilt whenever a header row is seen, and cleared at the first line that is not part of
        // a table, so a "Package" column in one table cannot be read across into the next.
        var packageColumns = new List<int>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;

            CollectFromProse(line, relativePath, lineNumber, citations);

            if (!line.TrimStart().StartsWith('|'))
            {
                packageColumns.Clear();
                continue;
            }

            var cells = SplitRow(line);

            if (IsSeparatorRow(cells))
            {
                continue;
            }

            var headers = HeaderColumns(cells);
            if (headers.Count > 0)
            {
                packageColumns = headers;
                continue;
            }

            foreach (var column in packageColumns)
            {
                if (column >= cells.Count)
                {
                    continue;
                }

                var cell = BareBacktickedPackage().Match(cells[column]);
                if (cell.Success)
                {
                    citations.Add(new Citation(
                        cell.Groups["package"].Value, relativePath, lineNumber, "package-column cell"));
                }
            }
        }
    }

    /// <summary>
    /// The two non-table positions: an install command anywhere on the line, and the
    /// <c>**Used in:**</c> attribution oss-libraries uses.
    /// </summary>
    /// <param name="line">The line to scan.</param>
    /// <param name="relativePath">The page, for the failure message.</param>
    /// <param name="lineNumber">The line number, for the failure message.</param>
    /// <param name="citations">The accumulating result.</param>
    private static void CollectFromProse(
        string line, string relativePath, int lineNumber, List<Citation> citations)
    {
        foreach (Match match in InstallCommand().Matches(line))
        {
            citations.Add(new Citation(
                match.Groups["package"].Value, relativePath, lineNumber, "install command"));
        }

        var usedIn = UsedInLine().Match(line);
        if (!usedIn.Success)
        {
            return;
        }

        foreach (Match match in BacktickedPackage().Matches(usedIn.Groups["rest"].Value))
        {
            citations.Add(new Citation(
                match.Groups["package"].Value, relativePath, lineNumber, "'Used in:' attribution"));
        }
    }

    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        var inner = trimmed.Trim('|');

        return inner.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static bool IsSeparatorRow(List<string> cells)
    {
        foreach (var cell in cells)
        {
            if (cell.Length == 0)
            {
                continue;
            }

            foreach (var character in cell)
            {
                if (character is not ('-' or ':'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<int> HeaderColumns(List<string> cells)
    {
        var columns = new List<int>();

        for (var index = 0; index < cells.Count; index++)
        {
            if (PackageColumnHeaders.Contains(cells[index]))
            {
                columns.Add(index);
            }
        }

        return columns;
    }
}
