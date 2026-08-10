using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that no connector under <c>src/Rag.NET.DataProviders*</c> writes a reserved metadata
/// key by hand into a <c>Dictionary&lt;string, string&gt;</c> it hands to <c>FileHandle.Metadata</c>
/// / <c>FileEntry.Metadata</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards against.</b> <c>RagPipelineExtensions.BuildMetadata</c> applies connector
/// tags first, with <c>TryAdd</c>, so a hand-written tag using a reserved key does not lose to the
/// framework value — it shadows it, and the collision throws
/// <c>Rag.NET.Models.ReservedMetadataKeyException</c> at ingest time, not at compile time. Phase
/// 4.10 Task 4 found five live instances of exactly this (Asana, Jira, Notion, Zendesk Articles,
/// Zendesk Tickets all wrote <c>metadata["updated_at"]</c> by hand) the moment <c>updated_at</c>
/// joined <c>ReservedMetadataKeys.AllKeys</c>. This test is what stops a sixth connector
/// reintroducing the same hazard for <c>updated_at</c> or any other reserved key, silently, until
/// it runs against a live service.
/// </para>
/// <para>
/// <b>Hardcoded key list, not a project reference.</b> This project deliberately references no
/// project in the repository — see the csproj comment — so these tests observe the working tree
/// exactly as it sits on disk, independent of whether anything currently compiles. The list below
/// must therefore be kept in sync with
/// <c>Rag.NET.Abstractions.Models.ReservedMetadataKeys.All</c> by hand; a mismatch here is a hole
/// in the guard, not a build failure, so nothing else will catch it.
/// </para>
/// </remarks>
public sealed partial class ReservedMetadataKeyGuardTests
{
    /// <summary>Mirrors <c>ReservedMetadataKeys.All</c>. See the type remarks for why hardcoded.</summary>
    private static readonly string[] ReservedKeys =
    [
        "document_id",
        "file_name",
        "created_at",
        "updated_at",
        "provider_id",
        "_parentKey",
        "allowed_roles",
        "trust_level",
        "page",
        "page_end",
    ];

    /// <summary>
    /// Directories under <c>src/</c> starting with this are connector assemblies. Includes the
    /// shared base (<c>Rag.NET.DataProviders</c> itself), which is deliberately in scope: a
    /// helper it ships is exactly where a reserved-key literal could hide from every
    /// per-connector review.
    /// </summary>
    private const string DataProvidersDirectoryGlob = "Rag.NET.DataProviders*";

    /// <summary>
    /// 19 connector directories exist under <c>src/</c> today (including the shared base). Far
    /// fewer means the scan lost the tree and would pass over nothing — silently, forever.
    /// </summary>
    private const int FewestPlausibleConnectorDirectories = 15;

    [Fact]
    public void NoDataProviderWritesAReservedMetadataKeyByHand()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var srcDirectory = Path.Combine(repositoryRoot, "src");
        var connectorDirectories = Directory.EnumerateDirectories(srcDirectory, DataProvidersDirectoryGlob).ToList();

        Assert.True(
            connectorDirectories.Count >= FewestPlausibleConnectorDirectories,
            $"Found only {connectorDirectories.Count} directories matching " +
            $"'{DataProvidersDirectoryGlob}' under src/, expected at least " +
            $"{FewestPlausibleConnectorDirectories}. A guard that scans nothing passes for the " +
            "wrong reason, so this fails instead.");

        var violations = new List<string>();
        foreach (var directory in connectorDirectories)
        {
            CollectViolations(repositoryRoot, directory, violations);
        }

        Assert.True(
            violations.Count == 0,
            "The following connector source lines write a reserved metadata key by hand. " +
            "RagPipelineExtensions.BuildMetadata applies connector tags first, so a reserved key " +
            "does not lose to the framework value — it shadows it, and the collision throws " +
            "ReservedMetadataKeyException at ingest time, against a live service, not at compile " +
            "time. Set the typed FileHandle.CreatedAt/UpdatedAt field (or the equivalent " +
            "framework-owned channel) instead of writing the reserved key into Metadata:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", violations));
    }

    private static void CollectViolations(string repositoryRoot, string directory, List<string> violations)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, directory))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');

            for (var i = 0; i < lines.Length; i++)
            {
                var match = ReservedKeyAssignment().Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var key = match.Groups["key"].Value;
                if (Array.IndexOf(ReservedKeys, key) < 0)
                {
                    continue;
                }

                violations.Add($"{relative}:{i + 1}: writes reserved key '{key}' by hand");
            }
        }
    }

    /// <summary>
    /// Matches a dictionary-indexer assignment whose key is a quoted literal — the shape both
    /// <c>metadata["updated_at"] = value;</c> and the object-initialiser form
    /// <c>["updated_at"] = value,</c> take. The negative lookahead excludes <c>==</c> (a
    /// comparison, not an assignment) and <c>=&gt;</c> (a lambda arrow that happens to follow a
    /// bracketed literal).
    /// </summary>
    /// <returns>The compiled matcher.</returns>
    // Deliberately no RegexOptions.NonBacktracking: the .NET NonBacktracking (symbolic) engine
    // does not support negative lookahead, which this pattern needs to exclude '==' and '=>'.
    [GeneratedRegex(@"\[\s*""(?<key>[A-Za-z_][A-Za-z0-9_]*)""\s*\]\s*=(?![=>])",
        RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReservedKeyAssignment();

    private static bool IsBuildOutput(string file, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, file);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
    }
}
