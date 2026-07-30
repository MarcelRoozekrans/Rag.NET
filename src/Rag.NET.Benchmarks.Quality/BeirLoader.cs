using System.Globalization;
using System.Text.Json;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Reads BEIR's on-disk layout: <c>corpus.jsonl</c> (<c>_id</c>, <c>title</c>, <c>text</c>),
/// <c>queries.jsonl</c> (<c>_id</c>, <c>text</c>) and <c>qrels/{split}.tsv</c>
/// (<c>query-id</c>, <c>corpus-id</c>, <c>score</c>, behind a header row).
/// <para>
/// Every parse failure throws and names the file and line. That is deliberate and it is the same
/// concern as verifying the download: a loader that tolerates a malformed or truncated file yields
/// a short corpus, which scores badly, which looks exactly like a retrieval defect and sends the
/// next person into the wrong half of the system.
/// </para>
/// </summary>
public static class BeirLoader
{
    /// <summary>
    /// The separator between a document's title and its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing: it changes what gets embedded and therefore shifts the parity number.
    /// </para>
    /// <para>
    /// <b>A single space, which is what BEIR actually does.</b> As of the current
    /// <c>beir/retrieval/models/sentence_bert.py</c>, <c>SentenceBERT</c> declares
    /// <c>sep: str = " "</c> and builds each corpus sentence as
    /// <c>(doc["title"] + sep + doc["text"]).strip()</c>. Phase 3.7's design and implementation plan
    /// both assert a newline; both are wrong, checked against upstream, and the published ≈ 0.645
    /// was produced with the space. The default was corrected in Task 5 rather than left as written.
    /// </para>
    /// <para>
    /// Still a parameter, because the alternative is worth being able to measure. Both were, through
    /// the shipped harness: the space gives SciFact nDCG@10 = <b>0.64593</b> and the newline
    /// <b>0.64907</b>, a difference of <b>0.00314</b>. Both land inside the ±0.02 parity band, so the
    /// test would have passed either way — which is precisely why the separator had to be checked
    /// against upstream instead of inferred from a green run.
    /// </para>
    /// </remarks>
    public const string DefaultTitleTextSeparator = " ";

    /// <summary>The header BEIR writes as the first line of every qrels TSV.</summary>
    /// <remarks>
    /// Required, not merely skipped. Blindly dropping line 1 silently eats a real judgement from a
    /// file that has no header; blindly keeping it turns the literal text <c>query-id</c> into a
    /// query. Both are off-by-one errors on exactly one query, which is invisible in a mean over
    /// 300 — so the header is asserted instead.
    /// </remarks>
    public const string QrelsHeader = "query-id\tcorpus-id\tscore";

    /// <summary>
    /// Loads a whole dataset from an extracted BEIR directory.
    /// </summary>
    /// <param name="datasetDirectory">The directory holding <c>corpus.jsonl</c>, <c>queries.jsonl</c> and <c>qrels/</c>.</param>
    /// <param name="split">The qrels split to load, without the extension. Defaults to <c>test</c>.</param>
    /// <param name="titleTextSeparator">Overrides <see cref="DefaultTitleTextSeparator"/>.</param>
    /// <returns>The corpus, every query, and the split's judgements.</returns>
    public static BeirDataset Load(
        string datasetDirectory, string split = "test", string titleTextSeparator = DefaultTitleTextSeparator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);

        return new BeirDataset(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(datasetDirectory)),
            split,
            LoadCorpus(Path.Combine(datasetDirectory, "corpus.jsonl"), titleTextSeparator),
            LoadQueries(Path.Combine(datasetDirectory, "queries.jsonl")),
            LoadQrels(Path.Combine(datasetDirectory, "qrels", split + ".tsv")));
    }

    /// <summary>
    /// Reads <c>corpus.jsonl</c>, joining each document's title and text into
    /// <see cref="BeirDocument.RetrievalText"/>.
    /// </summary>
    /// <param name="corpusPath">The path of <c>corpus.jsonl</c>.</param>
    /// <param name="titleTextSeparator">
    /// What to put between title and text. See <see cref="DefaultTitleTextSeparator"/> — including
    /// the recorded disagreement with BEIR's own default.
    /// </param>
    /// <returns>Every document, in file order.</returns>
    /// <exception cref="InvalidDataException">A line is not an object, or lacks <c>_id</c> or <c>text</c>.</exception>
    public static IReadOnlyList<BeirDocument> LoadCorpus(
        string corpusPath, string titleTextSeparator = DefaultTitleTextSeparator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusPath);
        ArgumentNullException.ThrowIfNull(titleTextSeparator);

        var documents = new List<BeirDocument>();
        var lineNumber = 0;

        foreach (var line in File.ReadLines(corpusPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var json = ParseLine(line, corpusPath, lineNumber);
            var title = OptionalString(json.RootElement, "title");
            var text = RequiredString(json.RootElement, "text", corpusPath, lineNumber);

            documents.Add(new BeirDocument(
                RequiredString(json.RootElement, "_id", corpusPath, lineNumber),
                title,
                text,
                string.Concat(title, titleTextSeparator, text).Trim()));
        }

        return RequireNonEmpty(documents, corpusPath, "documents");
    }

    /// <summary>Reads <c>queries.jsonl</c>.</summary>
    /// <param name="queriesPath">The path of <c>queries.jsonl</c>.</param>
    /// <returns>Every query, in file order — judged or not.</returns>
    /// <exception cref="InvalidDataException">A line is not an object, or lacks <c>_id</c> or <c>text</c>.</exception>
    public static IReadOnlyList<BeirQuery> LoadQueries(string queriesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queriesPath);

        var queries = new List<BeirQuery>();
        var lineNumber = 0;

        foreach (var line in File.ReadLines(queriesPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var json = ParseLine(line, queriesPath, lineNumber);
            queries.Add(new BeirQuery(
                RequiredString(json.RootElement, "_id", queriesPath, lineNumber),
                RequiredString(json.RootElement, "text", queriesPath, lineNumber)));
        }

        return RequireNonEmpty(queries, queriesPath, "queries");
    }

    /// <summary>
    /// Reads one qrels TSV into the shape <see cref="IrMetrics.Evaluate"/> takes.
    /// </summary>
    /// <param name="qrelsPath">The path of <c>qrels/{split}.tsv</c>.</param>
    /// <returns>Judgements by query id, then by document id.</returns>
    /// <exception cref="InvalidDataException">
    /// The header is missing or misspelt, a row does not have three tab-separated columns, or a
    /// score is not an integer.
    /// </exception>
    /// <remarks>
    /// Rows scoring 0 are kept. A 0 is a judgement that the document does NOT answer the query, and
    /// <see cref="IrMetrics"/> is what decides it earns no gain — dropping it here would hide the
    /// distinction between "judged irrelevant" and "never judged" from every metric downstream.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LoadQrels(string qrelsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qrelsPath);

        var judgements = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var lineNumber = 0;

        foreach (var line in File.ReadLines(qrelsPath))
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                RequireHeader(line, qrelsPath);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var (queryId, documentId, score) = ParseJudgement(line, qrelsPath, lineNumber);
            if (!judgements.TryGetValue(queryId, out var forQuery))
            {
                forQuery = new Dictionary<string, int>(StringComparer.Ordinal);
                judgements[queryId] = forQuery;
            }

            forQuery[documentId] = score;
        }

        if (judgements.Count == 0)
        {
            throw new InvalidDataException(
                $"'{qrelsPath}' contains a header but no judgements. Every query would then be " +
                "unjudged and there would be nothing to score.");
        }

        var byQuery = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (queryId, forQuery) in judgements)
        {
            byQuery[queryId] = forQuery;
        }

        return byQuery;
    }

    private static void RequireHeader(string line, string qrelsPath)
    {
        if (string.Equals(line.Trim(), QrelsHeader, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException(
            $"'{qrelsPath}' line 1 is '{line}' but BEIR qrels files begin with the header " +
            $"'{QrelsHeader.Replace("\t", "\\t", StringComparison.Ordinal)}'. The header is required " +
            "rather than merely skipped: skipping line 1 of a file that has no header silently drops " +
            "a real judgement, and keeping it turns the text 'query-id' into a query. Either way one " +
            "query is wrong and the mean over 300 hides it.");
    }

    private static (string QueryId, string DocumentId, int Score) ParseJudgement(
        string line, string qrelsPath, int lineNumber)
    {
        var columns = line.Split('\t');
        if (columns.Length != 3)
        {
            throw new InvalidDataException(
                $"'{qrelsPath}' line {lineNumber} has {columns.Length} tab-separated columns; BEIR " +
                "qrels rows have exactly three: query-id, corpus-id, score.");
        }

        if (!int.TryParse(columns[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
        {
            throw new InvalidDataException(
                $"'{qrelsPath}' line {lineNumber} has score '{columns[2]}', which is not an integer.");
        }

        return (columns[0].Trim(), columns[1].Trim(), score);
    }

    private static JsonDocument ParseLine(string line, string path, int lineNumber)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' line {lineNumber} is not valid JSON: {ex.Message}", ex);
        }

        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            json.Dispose();
            throw new InvalidDataException(
                $"'{path}' line {lineNumber} is a {json.RootElement.ValueKind}, not a JSON object.");
        }

        return json;
    }

    private static string RequiredString(JsonElement element, string propertyName, string path, int lineNumber)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString()!;
        }

        throw new InvalidDataException(
            $"'{path}' line {lineNumber} has no string '{propertyName}' property. BEIR JSONL records " +
            "carry _id and text; a file that does not is the wrong file, or a partial download.");
    }

    private static string OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : string.Empty;

    /// <summary>
    /// An empty corpus or query file is treated as corruption rather than as an empty dataset.
    /// </summary>
    /// <remarks>
    /// This is the truncated-download symptom seen from the other end: a short or empty corpus
    /// produces a terrible score that reads as a retrieval failure. Failing here names the actual
    /// cause.
    /// </remarks>
    private static IReadOnlyList<T> RequireNonEmpty<T>(List<T> items, string path, string what)
    {
        if (items.Count == 0)
        {
            throw new InvalidDataException(
                $"'{path}' yielded no {what}. An empty {what} file scores zero on every query, which " +
                "looks like a retrieval failure rather than a missing or truncated download.");
        }

        return items;
    }
}
