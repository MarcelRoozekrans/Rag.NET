using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Every test that skips on a variable <c>env.sh</c> provisions must say so in its skip message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Three separate sessions recorded this machine as unprovisioned while the
/// corpus sat at <c>~/.cache/ragnet-beir</c> with an <c>env.sh</c> beside it — the third after a
/// note saying two sessions had already done it. The skips were correct; the messages were dead
/// ends. Sourcing that script takes <c>Rag.NET.Embeddings.Onnx.Tests</c> from 10 skips to 0.
/// </para>
/// <para>
/// <b>Why the inventory is a variable list and not a file list.</b> This guard replaced one that
/// pinned four file paths by hand. It guarded the wirings that existed and could not notice a fifth
/// site that never got one —
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/575">#575</see>, raised against
/// exactly that shape. Keying on the variables inverts the maintenance burden the right way: a new
/// test gating on <c>RAGNET_ONNX_EMBED_VOCAB</c> is caught the day it is written, while adding a
/// variable is a deliberate one-line decision here. <c>SecurityDocumentationTests</c> derives its
/// set from the filesystem for the same reason.
/// </para>
/// <para>
/// <b>Why the list is hard-coded rather than read from <c>env.sh</c>.</b> CI has no copy of that
/// script — it is a local provisioning convenience — so the ground truth cannot be read at run
/// time. These five names were taken from the real script on 2026-09-13.
/// </para>
/// <para>
/// <b>Why matching the sentence would have been wrong.</b> Seventeen test files carry a
/// <c>"Set RAGNET_…"</c> skip message, but most gate on Whisper, Tesseract or Azure Document
/// Intelligence settings that <c>env.sh</c> does not provision. **Telling those readers to source it
/// would be a false claim**, so a guard keyed on the phrasing would have demanded a wrong message in
/// six places. #575 found two missing sites by searching for an identical sentence; keying on the
/// variables found five.
/// </para>
/// <para>
/// <b>Indirection counts.</b> A file satisfies this by composing the hint itself, or by using a
/// shared skip reason that already carries it — <c>BeirHarness.SkipReason</c> does, and roughly
/// thirty cases route through it. A guard that ignored that would have demanded a redundant second
/// hint in every one of them.
/// </para>
/// <para>
/// <b>What this cannot check.</b> That the composed sentence is accurate, or that the hint lands in
/// the message a reader actually sees rather than in some unused local. It checks that a file
/// gating on a provisioned variable has the hint available at all, which is the floor #575 was
/// filed about.
/// </para>
/// </remarks>
public sealed partial class SkipReasonWiringTests
{
    /// <summary>The variables <c>~/.cache/ragnet-beir/env.sh</c> exports, read from it 2026-09-13.</summary>
    /// <remarks>
    /// Adding one here is the deliberate decision this guard is built around: it immediately
    /// requires every test gating on that variable to name the script. Removing one silently
    /// narrows the guard, which is why the count is asserted below.
    /// </remarks>
    private static readonly string[] ProvisionedVariables =
    [
        "RAGNET_BEIR_CACHE",
        "RAGNET_ONNX_EMBED_MODEL",
        "RAGNET_ONNX_EMBED_VOCAB",
        "RAGNET_ONNX_RERANK_MODEL",
        "RAGNET_ONNX_RERANK_VOCAB",
    ];

    /// <summary>Ways a file can carry the hint, directly or through a shared skip reason.</summary>
    private static readonly string[] HintCalls =
    [
        "BeirProvisioningHint.Describe()",
        "DescribeUnreferencedConventionalCache()",
        "BeirHarness.SkipReason",
        "BeirHarness.RerankerSkipReason",
    ];

    /// <summary>
    /// Below this, the search has broken rather than the suites having stopped gating on anything.
    /// </summary>
    private const int FewestPlausibleGatedFiles = 6;

    /// <summary>A test gating on a provisioned variable must be able to name the script.</summary>
    [Fact]
    public void EveryTestGatingOnAProvisionedVariableCarriesTheHint()
    {
        Assert.Equal(5, ProvisionedVariables.Length);

        var gated = Scan(out var missing);

        Assert.True(
            gated.Count >= FewestPlausibleGatedFiles,
            $"Only {gated.Count} test files were found gating on a variable env.sh provisions, " +
            $"which is below {FewestPlausibleGatedFiles}. The suites shrinking that far is less " +
            "likely than this search having broken, and a guard that matches nothing passes " +
            "exactly when it has stopped guarding.");

        Assert.True(
            missing.Count == 0,
            "These test files skip on a variable ~/.cache/ragnet-beir/env.sh provisions, but their " +
            "skip message cannot tell a reader the script is sitting right there — the dead end " +
            "that had three sessions record a provisioned machine as unprovisioned:\n  " +
            string.Join("\n  ", missing) +
            "\n\nAppend BeirProvisioningHint.Describe() to the skip message, or route it through a " +
            "shared skip reason that already does. Do NOT add the hint to a gate on a variable " +
            "env.sh does not set — Whisper, Tesseract and Document Intelligence settings among " +
            "them — because telling that reader to source it would be a false claim.");
    }

    /// <summary>Whether <paramref name="source"/> contains any of <paramref name="needles"/>.</summary>
    /// <param name="source">The file text to search.</param>
    /// <param name="needles">The candidates.</param>
    /// <returns><see langword="true"/> on the first hit.</returns>
    /// <remarks>
    /// A plain loop rather than LINQ: this runs once per test source file, and ZA0601 forbids an
    /// allocating LINQ call inside a loop.
    /// </remarks>
    private static bool ContainsAny(string source, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (source.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Matches a conditional skip call, parenthesis included.</summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(@"Assert\.Skip(?:When|Unless)\(", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SkipGateCall();

    /// <summary>Finds every test file gating on a provisioned variable.</summary>
    /// <param name="missing">Receives those whose skip message cannot name the script.</param>
    /// <returns>Every gated file, relative to <c>tests/</c>.</returns>
    private static List<string> Scan(out List<string> missing)
    {
        var testsRoot = Path.Combine(TestProject.FindRepositoryRoot(), "tests");
        var gated = new List<string>();
        missing = [];

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);

            // A file only qualifies if it actually CALLS a skip. The trailing "(" is
            // load-bearing: a substring match on "Assert.Skip" flagged TestGateTests and
            // BeirRunBudget, which mention "<c>Assert.SkipWhen</c>" in doc comments while
            // skipping on nothing. Same call shape TestGateTests' own SkipGateCall() matches,
            // deliberately — two guards disagreeing about what a skip site is would be worse
            // than either being slightly wrong.
            if (!SkipGateCall().IsMatch(source))
            {
                continue;
            }

            if (!ContainsAny(source, ProvisionedVariables))
            {
                continue;
            }

            var relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');
            gated.Add(relative);

            if (!ContainsAny(source, HintCalls))
            {
                missing.Add(relative);
            }
        }


        return gated;
    }
}
