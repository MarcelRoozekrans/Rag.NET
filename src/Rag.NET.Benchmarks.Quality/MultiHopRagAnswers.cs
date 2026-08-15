using System.Text.Json;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The gold answers MultiHop-RAG publishes beside its queries, kept as a sidecar of the converted
/// dataset so answers can be scored against them.
/// <para>
/// <b>A sidecar rather than a column, because BEIR has no column for it.</b> The layout
/// <see cref="BeirLoader"/> reads is corpus, queries and qrels; nothing in it can hold an answer,
/// and adding one to <see cref="BeirQuery"/> would put a MultiHop-RAG-only field on every dataset.
/// So the conversion writes <c>answers.jsonl</c> beside <c>queries.jsonl</c> — one
/// <c>{"_id", "answer", "question_type"}</c> per query, in the same order and under the same
/// ids — and the one place that scores answers reads it back through <see cref="Load"/>.
/// </para>
/// <para>
/// <b>It is part of the dataset, and a cache without it is incomplete.</b>
/// <see cref="MultiHopRagSource.IsComplete"/> says so, which is what makes an older cache
/// re-convert rather than load and then fail in the first test that asks for an answer.
/// </para>
/// </summary>
public static class MultiHopRagAnswers
{
    /// <summary>The sidecar's file name, beside <c>queries.jsonl</c>.</summary>
    public const string FileName = "answers.jsonl";

    /// <summary>The <c>question_type</c> value of an inference query, whose answer is an entity.</summary>
    public const string InferenceType = "inference_query";

    /// <summary>The <c>question_type</c> value of a comparison query, whose answer is yes or no.</summary>
    public const string ComparisonType = "comparison_query";

    /// <summary>The <c>question_type</c> value of a temporal query, whose answer is yes/no or before/after.</summary>
    public const string TemporalType = "temporal_query";

    /// <summary>The <c>question_type</c> value of a null query, whose answer is "Insufficient information."</summary>
    public const string NullType = "null_query";

    /// <summary>Reports whether the sidecar exists in a converted dataset directory.</summary>
    /// <param name="datasetDirectory">The dataset directory.</param>
    /// <returns><see langword="true"/> when <see cref="FileName"/> is there.</returns>
    public static bool IsPresentAt(string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);

        return File.Exists(Path.Combine(datasetDirectory, FileName));
    }

    /// <summary>Reads every gold answer, by query id.</summary>
    /// <param name="datasetDirectory">The converted dataset directory.</param>
    /// <returns>The answers, keyed by the query id <c>queries.jsonl</c> uses.</returns>
    /// <exception cref="FileNotFoundException">The sidecar is not there.</exception>
    /// <exception cref="InvalidDataException">A line lacks one of the three properties, or an id repeats.</exception>
    public static IReadOnlyDictionary<string, MultiHopRagAnswer> Load(string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);

        var path = Path.Combine(datasetDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{path}' is not there. The gold answers are written by MultiHopRagConversion " +
                "beside queries.jsonl; a dataset directory without them predates the sidecar and " +
                "BeirDatasetCache re-converts it once MultiHopRagSource.IsComplete says so.",
                path);
        }

        var answers = new Dictionary<string, MultiHopRagAnswer>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = Required(root, "_id", path, lineNumber);
            var answer = new MultiHopRagAnswer(
                id, Required(root, "answer", path, lineNumber), Required(root, "question_type", path, lineNumber));

            if (!answers.TryAdd(id, answer))
            {
                throw new InvalidDataException(
                    $"'{path}' line {lineNumber} repeats query id '{id}'. Each query has one gold " +
                    "answer; a repeat means the sidecar and queries.jsonl were written from " +
                    "different runs.");
            }
        }

        return answers;
    }

    private static string Required(JsonElement element, string property, string path, int lineNumber) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException(
                $"'{path}' line {lineNumber} has no string '{property}'. Every line of the sidecar " +
                "carries _id, answer and question_type; a line without one was not written by the " +
                "conversion.");
}
