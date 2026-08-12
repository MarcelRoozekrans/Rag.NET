using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Turns MultiHop-RAG's two published JSON files into BEIR's on-disk layout, or refuses.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="MultiHopRagSource"/>, which downloads and verifies those
/// two files. Downloading and converting fail for unrelated reasons and only one of them needs a
/// network, so keeping them apart is what lets the conversion — the part with all the judgement in
/// it — be pinned against small synthetic fixtures in a unit test that never opens a socket.
/// </para>
/// <para>
/// <b>The output is staged and renamed, never written in place.</b>
/// <see cref="BeirDatasetCache.IsExtractedAt"/> asks for <c>corpus.jsonl</c>, <c>queries.jsonl</c>
/// and a <c>qrels/</c> directory; it does not ask for <c>qrels/test.tsv</c>. Writing straight into
/// the dataset directory and being interrupted after the first two files would therefore leave a
/// directory every later run reads as a cache hit — skipping acquisition, failing inside
/// <see cref="BeirLoader"/>, and never re-acquiring, because nothing re-acquires a dataset the cache
/// believes it has. Widening the cache's required files would fix this one dataset by changing the
/// cache-hit predicate for four that work today, so the discipline lives here instead: the directory
/// appears complete or it does not appear.
/// </para>
/// </remarks>
public static class MultiHopRagConversion
{
    /// <summary>
    /// The prefix every converted query id carries, ahead of the query's zero-padded position in
    /// the published file.
    /// </summary>
    /// <remarks>
    /// The position is assigned <b>before</b> any query is excluded from the judgements, so
    /// <c>mhr-0742</c> always names line 743 of <c>MultiHopRAG.json</c> — an id can be traced back
    /// to the record it came from without re-deriving whatever filter produced it.
    /// </remarks>
    public const string QueryIdPrefix = "mhr-";

    /// <summary>The split the judgements are written to; MultiHop-RAG publishes exactly one.</summary>
    public const string Split = "test";

    private const string QueryIdFormat = "D4";

    /// <summary>
    /// Converts one downloaded <c>corpus.json</c> and <c>MultiHopRAG.json</c> pair into
    /// <paramref name="datasetDirectory"/>.
    /// </summary>
    /// <param name="corpusJsonPath">The published <c>corpus.json</c>, already on disk.</param>
    /// <param name="queriesJsonPath">The published <c>MultiHopRAG.json</c>, already on disk.</param>
    /// <param name="datasetDirectory">
    /// Where the BEIR layout must end up. It is created by the rename that publishes it, so it must
    /// not already exist — which is what <see cref="IBeirDatasetSource"/> promises.
    /// </param>
    /// <param name="expected">
    /// What the conversion must produce. Nothing is published unless all four totals match.
    /// </param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="InvalidDataException">
    /// A file is not the array MultiHop-RAG publishes, a record lacks a property the conversion
    /// reads, two articles share a url, an evidence row cites a url no article carries, or the
    /// totals do not match <paramref name="expected"/>.
    /// </exception>
    public static async Task ConvertAsync(
        string corpusJsonPath,
        string queriesJsonPath,
        string datasetDirectory,
        MultiHopRagCounts expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(queriesJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        ArgumentNullException.ThrowIfNull(expected);

        using var corpusJson = await ReadArrayAsync(corpusJsonPath, cancellationToken).ConfigureAwait(false);
        using var queriesJson = await ReadArrayAsync(queriesJsonPath, cancellationToken).ConfigureAwait(false);

        var stagingDirectory = datasetDirectory + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".converting";

        try
        {
            _ = Directory.CreateDirectory(Path.Combine(stagingDirectory, "qrels"));

            var documentIds = WriteCorpus(corpusJson.RootElement, stagingDirectory, corpusJsonPath);
            var written = WriteQueriesAndQrels(
                queriesJson.RootElement, stagingDirectory, documentIds, queriesJsonPath);

            RequireExpectedTotals(expected, written, datasetDirectory);

            await PublishRename
                .PublishDirectoryAsync(stagingDirectory, datasetDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Writes <c>corpus.jsonl</c> and returns every article's id.
    /// </summary>
    /// <remarks>
    /// <b>The id is the article's url, not its title.</b> Both are bijective over the published 609
    /// and every evidence row joins on both, so either would work today. The url is chosen because a
    /// title can be silently re-worded upstream and still join, whereas a changed url stops joining
    /// and the conversion refuses — which is the behaviour worth having when the thing being
    /// converted is a live repository.
    /// </remarks>
    private static HashSet<string> WriteCorpus(
        JsonElement corpus, string stagingDirectory, string sourcePath)
    {
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;

        var writer = new BeirJsonlWriter(Path.Combine(stagingDirectory, "corpus.jsonl"));
        using (writer)
        {
            foreach (var article in corpus.EnumerateArray())
            {
                var url = RequiredString(article, "url", sourcePath, position);
                if (!documentIds.Add(url))
                {
                    throw new InvalidDataException(
                        $"'{sourcePath}' publishes two articles at '{url}'. The url is the document " +
                        "id, so the two would share one _id: every judgement naming it becomes " +
                        "ambiguous and one of the two articles is unreachable. The url is bijective " +
                        "over the pinned revision's 609 articles, so this is a changed upstream.");
                }

                writer.WriteRecord(
                    url,
                    RequiredString(article, "title", sourcePath, position),
                    RequiredString(article, "body", sourcePath, position));

                position++;
            }
        }

        return documentIds;
    }

    /// <summary>
    /// Writes <c>queries.jsonl</c> and <c>qrels/test.tsv</c> in one pass, and returns what it wrote.
    /// </summary>
    /// <remarks>
    /// Every query is written, judged or not. MultiHop-RAG's 301 <c>null_query</c> records carry an
    /// empty <c>evidence_list</c> and therefore earn no qrels row — which is not a special case in
    /// this loop, just the empty-list outcome, and is exactly what BEIR itself does with SciFact's
    /// 809 unjudged queries.
    /// </remarks>
    private static MultiHopRagCounts WriteQueriesAndQrels(
        JsonElement queries, string stagingDirectory, HashSet<string> documentIds, string sourcePath)
    {
        var queryCount = 0;
        var judgedQueryCount = 0;
        var judgementCount = 0;
        var relevant = new List<string>();
        var alreadyCited = new HashSet<string>(StringComparer.Ordinal);

        var queryWriter = new BeirJsonlWriter(Path.Combine(stagingDirectory, "queries.jsonl"));
        using (queryWriter)
        {
            var qrelsWriter = new StreamWriter(Path.Combine(stagingDirectory, "qrels", Split + ".tsv"))
            {
                NewLine = "\n",
            };

            using (qrelsWriter)
            {
                qrelsWriter.WriteLine(BeirLoader.QrelsHeader);

                foreach (var query in queries.EnumerateArray())
                {
                    var queryId = QueryIdPrefix +
                        queryCount.ToString(QueryIdFormat, CultureInfo.InvariantCulture);

                    queryWriter.WriteRecord(
                        queryId, title: null, RequiredString(query, "query", sourcePath, queryCount));

                    CollectEvidence(query, documentIds, relevant, alreadyCited, sourcePath, queryId);

                    foreach (ref readonly var documentId in CollectionsMarshal.AsSpan(relevant))
                    {
                        qrelsWriter.WriteLine(string.Concat(queryId, "\t", documentId, "\t1"));
                    }

                    judgedQueryCount += relevant.Count == 0 ? 0 : 1;
                    judgementCount += relevant.Count;
                    queryCount++;
                }
            }
        }

        return new MultiHopRagCounts(documentIds.Count, queryCount, judgedQueryCount, judgementCount);
    }

    /// <summary>
    /// Resolves one query's evidence rows to document ids, in first-cited order and without
    /// repeats.
    /// </summary>
    /// <remarks>
    /// A query citing two facts from the same article produces two evidence rows and one judgement:
    /// the judgements are binary, so the second row would be a duplicate rather than a stronger
    /// grade. Upstream's 6,084 rows collapse to 5,908 distinct pairs for exactly this reason.
    /// </remarks>
    private static void CollectEvidence(
        JsonElement query,
        HashSet<string> documentIds,
        List<string> relevant,
        HashSet<string> alreadyCited,
        string sourcePath,
        string queryId)
    {
        relevant.Clear();
        alreadyCited.Clear();

        if (!query.TryGetProperty("evidence_list", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"'{sourcePath}' query {queryId} has no 'evidence_list' array. Every MultiHop-RAG " +
                "query carries one, empty for a null_query; a record without it is a different " +
                "file, or a partial download.");
        }

        foreach (var row in evidence.EnumerateArray())
        {
            var url = RequiredString(row, "url", sourcePath, queryId);
            if (!documentIds.Contains(url))
            {
                throw new InvalidDataException(
                    $"'{sourcePath}' query {queryId} cites evidence published at '{url}', which no " +
                    "article in corpus.json carries. Skipping the row would write a smaller qrels " +
                    "file and report a plausible, wrong number — recall measured against " +
                    "judgements the dataset does not contain. All 6,084 evidence rows of the pinned " +
                    "revision join, so this is a changed upstream or a partial download.");
            }

            if (alreadyCited.Add(url))
            {
                relevant.Add(url);
            }
        }
    }

    /// <summary>
    /// Refuses to publish a conversion whose totals are not the ones the revision holds.
    /// </summary>
    /// <remarks>
    /// The last check, and the one that catches everything the others do not. A converter that
    /// silently writes less does not crash: it writes a directory that loads, scores, and yields a
    /// believable figure computed from less data than the figure claims. Checked before the rename,
    /// so a conversion that fails here publishes nothing at all.
    /// </remarks>
    private static void RequireExpectedTotals(
        MultiHopRagCounts expected, MultiHopRagCounts written, string datasetDirectory)
    {
        if (expected == written)
        {
            return;
        }

        throw new InvalidDataException(
            $"Converting MultiHop-RAG for '{datasetDirectory}' produced " +
            $"{Describe(written)}, but the pinned revision holds {Describe(expected)}. Nothing was " +
            "published: a short conversion loads and scores like a complete one, so the difference " +
            "would otherwise surface as a retrieval figure nobody can tell is wrong.");
    }

    private static string Describe(MultiHopRagCounts counts) => string.Create(
        CultureInfo.InvariantCulture,
        $"{counts.DocumentCount} documents, {counts.QueryCount} queries, {counts.JudgedQueryCount} judged queries and {counts.JudgementCount} qrels rows");

    /// <summary>Reads one published file, which must be a JSON array.</summary>
    private static async Task<JsonDocument> ReadArrayAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var json = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                return json;
            }

            var kind = json.RootElement.ValueKind;
            json.Dispose();

            throw new InvalidDataException(
                $"'{path}' is a JSON {kind}, not the array MultiHop-RAG publishes. Converting it " +
                "would yield an empty dataset, which scores zero and reads as a retrieval defect.");
        }
    }

    private static string RequiredString(JsonElement element, string propertyName, string sourcePath, int position)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString()!;
        }

        throw new InvalidDataException(
            $"'{sourcePath}' record {position.ToString(CultureInfo.InvariantCulture)} has no string " +
            $"'{propertyName}' property. MultiHop-RAG's records carry it; a file whose records do " +
            "not is the wrong file, or a partial download.");
    }

    private static string RequiredString(JsonElement element, string propertyName, string sourcePath, string queryId)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString()!;
        }

        throw new InvalidDataException(
            $"'{sourcePath}' query {queryId} has a record with no string '{propertyName}' property. " +
            "MultiHop-RAG's evidence rows carry an article's own keys plus 'fact'; a row without " +
            "one is the wrong file, or a partial download.");
    }

    /// <summary>
    /// Writes BEIR's JSONL: one <c>{"_id", "title", "text"}</c> object per line, newline-delimited.
    /// </summary>
    /// <remarks>
    /// A <see cref="Utf8JsonWriter"/> rather than string concatenation, so escaping is the
    /// serializer's problem and not this file's — MultiHop-RAG is a news corpus and its bodies carry
    /// quotes, newlines and non-ASCII punctuation that hand-built JSON gets wrong quietly. The
    /// writer is <see cref="Utf8JsonWriter.Reset()"/> between records because a single writer
    /// otherwise refuses to emit a second top-level value.
    /// </remarks>
    private sealed class BeirJsonlWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly Utf8JsonWriter _writer;

        public BeirJsonlWriter(string path)
        {
            _stream = File.Create(path);
            _writer = new Utf8JsonWriter(_stream);
        }

        /// <summary>Appends one record.</summary>
        /// <param name="id">The <c>_id</c>.</param>
        /// <param name="title">The <c>title</c>, or <see langword="null"/> to omit it — queries have none.</param>
        /// <param name="text">The <c>text</c>.</param>
        public void WriteRecord(string id, string? title, string text)
        {
            _writer.WriteStartObject();
            _writer.WriteString("_id", id);

            if (title is not null)
            {
                _writer.WriteString("title", title);
            }

            _writer.WriteString("text", text);
            _writer.WriteEndObject();
            _writer.Flush();

            _stream.WriteByte((byte)'\n');
            _writer.Reset();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
