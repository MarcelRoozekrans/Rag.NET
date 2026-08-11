using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Every options type reachable through an <c>Action&lt;TOptions&gt;</c> registration callback can
/// actually be assigned inside that callback.
/// <para>
/// <b>The defect this exists for.</b> Registration extensions construct the options and then invoke
/// the caller's callback on the finished instance:
/// </para>
/// <code>
/// var opts = new GmailOptions();
/// configure?.Invoke(opts);
/// </code>
/// <para>
/// An <c>init</c>-only property cannot be assigned there — <c>opts.UserName = "…"</c> is CS8852 —
/// so the callback is accepted, invoked, and can set nothing. `GmailOptions` shipped that way with
/// every user-facing property <c>init</c>-only, which meant `UserName` stayed empty through the one
/// public registration path the package offers and IMAP OAuth2 authenticated as nobody. The sweep
/// behind issue #142 found the same shape on eighteen further options types, 34 properties in all.
/// </para>
/// <para>
/// <b>Why a convention test rather than per-provider tests.</b> A test that registers a provider
/// and asserts it resolves passes throughout the defect: registration succeeds, a singleton
/// appears, and nothing observes that the options never took the caller's values. Catching it
/// per provider needs every provider to remember to assert configured values. Catching it here
/// needs nobody to remember anything — and it fails on a *new* provider written the same way,
/// which is the case per-provider tests cannot cover because the test would not exist yet.
/// </para>
/// </summary>
public sealed class ConfigureCallbackTests
{
    /// <summary>Options types whose configure callbacks are exercised, by declaring file.</summary>
    private static readonly Regex ConfigureCallback =
        new(@"Action<(?<options>\w+Options)>", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    /// <summary>A public auto-property with an <c>init</c> accessor.</summary>
    private static readonly Regex InitOnlyProperty =
        new(@"public\s+[^;{}\n]+?\s(?<name>\w+)\s*\{\s*get;\s*init;\s*\}",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static readonly Regex ClassDeclaration =
        new(@"\bclass (?<options>\w+Options)\b",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    [Fact]
    public void NoOptionsTypeReachedByAConfigureCallbackHasInitOnlyProperties()
    {
        var root = Path.Combine(TestProject.FindRepositoryRoot(), "src");
        var declarations = DeclaringFiles(root);
        var offenders = new List<string>();

        foreach (var optionsType in ConfigurableOptionsTypes(root))
        {
            if (!declarations.TryGetValue(optionsType, out var file))
            {
                continue;
            }

            var initOnly = new List<string>();
            foreach (Match property in InitOnlyProperty.Matches(File.ReadAllText(file)))
            {
                initOnly.Add(property.Groups["name"].Value);
            }

            if (initOnly.Count > 0)
            {
                offenders.Add(
                    $"{optionsType} ({Path.GetRelativePath(root, file).Replace('\\', '/')}): "
                    + string.Join(", ", initOnly));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These options types are handed to an Action<TOptions> configure callback, but the "
            + "properties below are init-only and so cannot be assigned inside it. The callback "
            + "would be accepted, invoked, and unable to set anything — the defect behind issue "
            + "#142, where a provider's own registration extension could not configure it. Use "
            + "'get; set;' on options a callback is meant to mutate:"
            + Environment.NewLine + "  - "
            + string.Join(Environment.NewLine + "  - ", offenders));
    }

    /// <summary>
    /// The scan sees a real configure callback, so an empty result cannot pass for compliance.
    /// </summary>
    [Fact]
    public void TheScanFindsTheRegistrationSurfaceItGuards()
    {
        var root = Path.Combine(TestProject.FindRepositoryRoot(), "src");
        var found = ConfigurableOptionsTypes(root);

        Assert.True(
            found.Count >= 30,
            $"Found only {found.Count} options types behind Action<TOptions> callbacks, which is "
            + "far below what this repository has. The scan has regressed, and a guard that "
            + "inspects nothing passes for the wrong reason.");
        Assert.Contains("GmailOptions", found);
    }

    private static HashSet<string> ConfigurableOptionsTypes(string root)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*Extensions.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            foreach (Match match in ConfigureCallback.Matches(File.ReadAllText(file)))
            {
                types.Add(match.Groups["options"].Value);
            }
        }

        return types;
    }

    private static Dictionary<string, string> DeclaringFiles(string root)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            foreach (Match match in ClassDeclaration.Matches(File.ReadAllText(file)))
            {
                _ = declarations.TryAdd(match.Groups["options"].Value, file);
            }
        }

        return declarations;
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
