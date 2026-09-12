using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins that a "BEIR cache present but unreferenced" hint, once composed, is actually wired into
/// the skip message a caller sees — not merely that the composing method produces the right text
/// in isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why source text, not a runtime assertion.</b> Every one of these hints degrades to an empty
/// string on a machine without <c>~/.cache/ragnet-beir</c> — every CI runner. Appending an empty
/// string is unobservable in the composed message no matter whether the call happened at all, so a
/// test that only compares a <c>SkipReason</c>'s runtime VALUE against the live hint cannot tell
/// "the call happened and returned empty" from "the call was deleted" on exactly the machines this
/// guard has to run on. <c>Rag.NET.Benchmarks.Quality.IntegrationTests.SkipMessageTests</c> still
/// carries that runtime composition test, and it is worth keeping — it catches a wrong composition
/// whenever the live hint happens to be non-null — but it cannot be the only guard, because it is
/// silent on every CI runner. Reading the source and asserting the call site exists in the
/// property's own expression body — not merely somewhere in the file, where a doc comment could
/// satisfy it — fails deterministically on any machine, with or without the corpus.
/// </para>
/// <para>
/// This is the same lesson <see cref="TestProject.ReadWorkflowCommands"/>'s own remark documents for
/// workflow YAML, applied to C#: a plain <c>Contains</c> over raw file text can be satisfied by a
/// comment rather than by code. <c>BeirHarness</c>'s own remark for its <c>SkipReason</c> names the
/// exact method this guard checks for, in prose, immediately above the property — so anchoring on
/// the property's own signature, and cutting at its terminating <c>;</c>, is load-bearing: it is
/// what keeps this guard from being satisfied by that doc comment instead of the code below it.
/// </para>
/// </remarks>
public sealed class SkipReasonWiringTests
{
    [Theory]
    [InlineData(
        "tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs",
        "public static string SkipReason =>",
        "BeirDatasetCache.DescribeUnreferencedConventionalCache()")]
    [InlineData(
        "tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs",
        "private static string SkipReason =>",
        "ConventionalCacheHint()")]
    [InlineData(
        "tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs",
        "private static string SkipReason =>",
        "ConventionalCacheHint()")]
    [InlineData(
        "tests/Rag.NET.Embeddings.Onnx.Tests/OnnxEmbeddingGeneratorSmokeTests.cs",
        "private static string SkipReason =>",
        "ConventionalCacheHint()")]
    public void TheSkipReasonPropertyCallsTheConventionalCacheHint(
        string repositoryRelativePath, string propertySignature, string requiredCall)
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(),
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);

        var signatureStart = source.IndexOf(propertySignature, StringComparison.Ordinal);
        Assert.True(
            signatureStart >= 0,
            $"{repositoryRelativePath} no longer declares '{propertySignature}'. Either the " +
            "property was renamed or restructured, or this guard is stale.");

        var terminator = source.IndexOf(';', signatureStart);
        Assert.True(
            terminator >= 0,
            $"{repositoryRelativePath}'s '{propertySignature}' has no terminating ';' before EOF.");

        // The slice starts AT the signature, not before it, so the property's own leading doc
        // comment — which may itself describe the call in prose — is excluded. Only the code
        // inside the expression body can satisfy the assertion below.
        var expressionBody = source[signatureStart..(terminator + 1)];

        Assert.Contains(requiredCall, expressionBody, StringComparison.Ordinal);
    }
}
