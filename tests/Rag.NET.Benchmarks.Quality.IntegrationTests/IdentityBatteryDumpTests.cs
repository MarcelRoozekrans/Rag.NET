using System.Globalization;
using Microsoft.Extensions.AI;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The .NET half of <c>identity_check.py</c>'s embedder-identity battery — the dump tool its
/// usage block names. The Python side writes one <c>&lt;name&gt;.input.txt</c> per battery string
/// (<c>uv run python identity_check.py --write-battery &lt;dir&gt;</c>); this fact embeds each
/// input's exact bytes with the generator behind every published figure and writes the vector to
/// <c>&lt;name&gt;.txt</c> beside it, which <c>identity_check.py &lt;dir&gt;</c> then compares
/// float for float against its own <c>PinnedEmbedder</c>.
/// <para>
/// <b>It is a test, not a console project, so it cannot drift from the measurements.</b> The one
/// thing the dump must guarantee is that its vectors come from the exact pipeline the published
/// BEIR figures used — <see cref="BeirHarness.CreateGenerator"/>, the single place that
/// configuration lives. A separate tool would wire <c>OnnxEmbeddingOptions</c> a second time, and
/// a second wiring can drift silently; calling the harness's own factory from the harness's own
/// test project cannot. Each text is embedded in a single-text call, the same batch shape the
/// Python side uses — pooling excludes padding so batching cannot change a vector, but matching
/// the shape keeps the comparison free of even that argument.
/// </para>
/// <para>
/// Opt-in like every environment-dependent case: it needs the pinned model
/// (<see cref="BeirHarness.IsProvisioned"/>) and skips unless
/// <c>RAGNET_IDENTITY_BATTERY_DIR</c> names the directory the battery inputs were written to. A
/// set variable pointing at a directory without inputs <b>fails</b> rather than skipping — that
/// state means the Python step was forgotten, and a green skip would read as a completed dump.
/// </para>
/// </summary>
public sealed class IdentityBatteryDumpTests
{
    /// <summary>The suffix the Python side gives battery inputs; the dump replaces it with .txt.</summary>
    private const string InputSuffix = ".input.txt";

    /// <summary>The message an un-opted-in run skips with.</summary>
    private const string SkipReason =
        "Set RAGNET_IDENTITY_BATTERY_DIR to the directory 'identity_check.py --write-battery' " +
        "filled with *.input.txt files to dump the .NET-side vectors it compares against.";

    private readonly ITestOutputHelper _output;

    public IdentityBatteryDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task DumpsEachBatteryInputsVector_ForIdentityCheckToCompare()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out _),
            BeirHarness.SkipReason);
        var directory =
            Environment.GetEnvironmentVariable("RAGNET_IDENTITY_BATTERY_DIR") ?? string.Empty;
        Assert.SkipWhen(directory.Length == 0, SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var inputFiles = Directory.GetFiles(directory, "*" + InputSuffix);
        Array.Sort(inputFiles, StringComparer.Ordinal);
        Assert.True(
            inputFiles.Length > 0,
            $"RAGNET_IDENTITY_BATTERY_DIR is set but '{directory}' holds no *{InputSuffix} " +
            "files. Run 'uv run python identity_check.py --write-battery <dir>' first — the " +
            "battery inputs are the Python side's, so both sides embed the same bytes.");

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        foreach (var inputFile in inputFiles)
        {
            var dumpFile = await DumpAsync(generator, inputFile, ct);
            _output.WriteLine($"{inputFile} -> {dumpFile}");
        }

        _output.WriteLine(
            $"Dumped {inputFiles.Length} vectors. Compare with: uv run python identity_check.py " +
            $"\"{directory}\"");
    }

    /// <summary>
    /// Embeds one battery input's exact bytes and writes its vector beside it: one
    /// invariant-culture float per line, the shortest round-trippable form, so
    /// <c>identity_check.py</c>'s <c>float(line)</c> recovers each float32 bit for bit.
    /// </summary>
    /// <returns>The dump file's path.</returns>
    private static async Task<string> DumpAsync(
        Rag.NET.Embeddings.Onnx.OnnxEmbeddingGenerator generator,
        string inputFile,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(inputFile, cancellationToken);
        var embedded = await generator.GenerateAsync([text], cancellationToken: cancellationToken);
        var vector = embedded[0].Vector.ToArray();

        var lines = new string[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            lines[i] = vector[i].ToString(CultureInfo.InvariantCulture);
        }

        var name = Path.GetFileName(inputFile);
        var dumpFile = Path.Combine(
            Path.GetDirectoryName(inputFile) ?? string.Empty,
            string.Concat(name.AsSpan(0, name.Length - InputSuffix.Length), ".txt"));
        await File.WriteAllLinesAsync(dumpFile, lines, cancellationToken);

        return dumpFile;
    }
}
