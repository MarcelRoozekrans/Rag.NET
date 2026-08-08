using System.Text.Json;

namespace Rag.NET.Cli;

/// <summary>
/// Formats <c>ragnet</c>'s output: usage text, and the JSON a command's result is written to
/// stdout as.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="IngestCommand"/>/<see cref="QueryCommand"/> so those stay pure
/// computations over an <see cref="Abstractions.IRagPipeline"/>, with no knowledge of where their
/// result ends up or how it is presented.
/// </remarks>
internal static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] KnownCommands = ["ingest", "query", "evaluate"];

    /// <summary>
    /// Why <c>evaluate</c> is not implemented. <c>Rag.NET.Evaluation</c>'s evaluators
    /// (<c>EmbeddingDistanceEvaluator</c>, <c>LlmJudgeEvaluator</c>) score
    /// <c>EvaluationSample</c> instances that already carry a predicted answer, and no
    /// <c>IRagEvaluator</c> is registered by <c>AddRagNetPipelineFromConfiguration</c>. Wiring a
    /// working command would mean designing a dataset file format and an evaluator-selection
    /// story that does not exist yet, not a thin call onto an existing seam — see this
    /// project's README.
    /// </summary>
    public const string EvaluateDeferredMessage =
        "'evaluate' is not implemented. Rag.NET.Evaluation's evaluators score samples that " +
        "already carry a predicted answer, and no IRagEvaluator is registered by " +
        "AddRagNetPipelineFromConfiguration — wiring this command needs a dataset file format " +
        "and an evaluator-selection decision this task deferred rather than half-building. " +
        "See this package's README.";

    /// <summary>Whether <paramref name="command"/> names one of <c>ragnet</c>'s commands.</summary>
    public static bool IsKnownCommand(string command)
    {
        foreach (var known in KnownCommands)
        {
            if (string.Equals(known, command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="writer"/> as indented JSON.</summary>
    public static void WriteJson<T>(TextWriter writer, T value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    /// <summary>Writes usage text to <paramref name="writer"/>.</summary>
    public static void WriteUsage(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("ragnet - command-line access to a configured Rag.NET pipeline.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  ragnet ingest <path> [--overwrite]   Ingest a file or a directory of files.");
        writer.WriteLine("  ragnet query <question> [--top-k N]  Retrieve chunks relevant to a question.");
        writer.WriteLine();
        writer.WriteLine("Configuration is read from appsettings.json (next to the working directory)");
        writer.WriteLine("or environment variables, under the RagNet section - see the README.");
    }
}
