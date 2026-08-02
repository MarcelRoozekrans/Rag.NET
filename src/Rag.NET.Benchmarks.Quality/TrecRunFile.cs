using System.Globalization;
using System.Text;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The run-file boundary of the Phase 3.14 library comparison: every entrant — Rag.NET, Semantic
/// Kernel, and the Python libraries (LangChain, LlamaIndex, Haystack) — emits a TREC-format run
/// file, and nothing else, and every run file is scored by the one <see cref="IrMetrics"/> that
/// produced this repository's published BEIR figures. One line per ranked document:
/// <code>
/// &lt;queryId&gt; Q0 &lt;docId&gt; &lt;rank&gt; &lt;score&gt; &lt;runTag&gt;
/// </code>
/// <c>Q0</c> is a literal the format requires; ranks are 1-based.
/// <para>
/// <b>Ranking is authoritative, not score.</b> The score column exists solely so an outside reader
/// can run <c>trec_eval</c> on a published run file and check our numbers. It is <b>not</b>
/// comparable across entrants and the comparison never uses it: cosine similarities, BM25 weights
/// and cross-encoder logits live on different scales, and an entrant whose scores look nothing
/// like another's is fine and expected. <see cref="Read"/> returns rank order and discards the
/// scores.
/// </para>
/// <para>
/// Because <c>trec_eval</c> ignores the rank column and re-sorts by score, the one state in which
/// our scoring and an outsider's cannot diverge is ranks <c>1..n</c> in file order with
/// non-increasing scores — so both <see cref="Write"/> and <see cref="Read"/> enforce exactly
/// that, and a file where score order contradicts rank order is rejected as self-disagreeing
/// rather than resolved either way. Ties are allowed and their emitted order is preserved;
/// <c>trec_eval</c>'s own tie-breaking is the kind of evaluator divergence this boundary exists
/// to contain.
/// </para>
/// <para>
/// <b>Every malformed line throws</b>, naming the file and line, in the same spirit as
/// <see cref="BeirLoader"/>: a silently skipped line is a ranking one document short, which is a
/// silently different nDCG with no error anywhere. That includes rank gaps — a run claiming ranks
/// 1, 2, 4 is an error, not a sparse ranking, because <see cref="Read"/> returns a list whose
/// position <i>is</i> the rank, so the document claiming rank 4 would silently become rank 3, and
/// a gap almost always means lines were filtered or lost after ranking.
/// </para>
/// <para>
/// <b>An empty run is representable and distinct from a missing query.</b> TREC's
/// one-line-per-document format has no native way to say "retrieved nothing" — zero lines for a
/// query is indistinguishable from a query the entrant never saw, and
/// <see cref="IrMetrics.Evaluate"/> treats the two differently. <see cref="Write"/> therefore
/// emits <c><see cref="EmptyRunDocumentId"/></c> at rank 1 for an empty run, and
/// <see cref="Read"/> folds it back to an empty list. The marker is harmless to an outsider's
/// <c>trec_eval</c> pass: an unjudged document id earns no gain there, and in an otherwise empty
/// run it displaces nothing.
/// </para>
/// <para>
/// Formatting and parsing are pinned to <see cref="CultureInfo.InvariantCulture"/>, and lines end
/// in <c>\n</c> with queries in ordinal id order — a published run file must be byte-identical no
/// matter which machine wrote it, and the development machine's culture writes <c>0,5</c> for a
/// half, which would either misparse or silently read as a different number elsewhere.
/// </para>
/// </summary>
public static class TrecRunFile
{
    /// <summary>
    /// The reserved document id that marks a query whose entrant retrieved nothing — see the class
    /// remarks for why the distinction from a missing query must survive the file format. Never a
    /// real document: <see cref="Write"/> rejects it in a ranking, and no BEIR corpus uses it.
    /// </summary>
    public const string EmptyRunDocumentId = "RAGNET-EMPTY-RUN";

    /// <summary>The literal the format fixes as every line's second field.</summary>
    private const string IterationLiteral = "Q0";

    private static readonly char[] FieldSeparators = [' ', '\t'];

    /// <summary>
    /// Writes one entrant's rankings as a TREC run file, validating that the result is a file
    /// <see cref="Read"/> and <c>trec_eval</c> will agree about.
    /// </summary>
    /// <param name="runFilePath">Where to write. Overwritten if present.</param>
    /// <param name="runs">
    /// Rankings by query id, best first — <see cref="DocumentRanking.TopDocuments"/>'s output
    /// shape. An empty list is a query that retrieved nothing and becomes an
    /// <see cref="EmptyRunDocumentId"/> marker line; a query the entrant never processed is simply
    /// absent, and the two survive the round trip as different things.
    /// </param>
    /// <param name="runTag">
    /// The entrant's name in every line's last field, e.g. <c>ragnet-default</c>. No whitespace —
    /// the format is whitespace-separated.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="runs"/> is empty (an entrant that lost its query set, not an empty
    /// ranking), an id or the tag contains whitespace, a ranking repeats a document, uses the
    /// reserved marker id, or has a score that rises where its ranks fall — which
    /// <c>trec_eval</c>, sorting by score, would read as a different ranking than the one written.
    /// </exception>
    public static void Write(
        string runFilePath,
        IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> runs,
        string runTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);
        ArgumentNullException.ThrowIfNull(runs);
        RequireToken(runTag, "run tag", nameof(runTag));
        if (runs.Count == 0)
        {
            throw new ArgumentException(
                "The run set is empty. An entrant that processed zero queries did not produce an " +
                "empty ranking; it lost its query set, and a run file written from it would score " +
                "zero everywhere and read as retrieval failure.",
                nameof(runs));
        }

        var queryIds = new string[runs.Count];
        var next = 0;
        foreach (var queryId in runs.Keys)
        {
            queryIds[next++] = queryId;
        }

        // Ordinal order, so the same rankings produce a byte-identical published file everywhere.
        Array.Sort(queryIds, StringComparer.Ordinal);

        var content = new StringBuilder();
        foreach (var queryId in queryIds)
        {
            AppendQuery(content, queryId, runs[queryId], runTag);
        }

        File.WriteAllText(runFilePath, content.ToString());
    }

    /// <summary>
    /// Reads a run file back to rankings by query id, best first — exactly the shape
    /// <see cref="IrMetrics.Evaluate"/> consumes.
    /// </summary>
    /// <param name="runFilePath">The run file to read.</param>
    /// <returns>
    /// Ranked document ids by query id. Rank order comes from the file's 1-based rank column,
    /// which must be contiguous per query; the scores are validated (invariant-culture numbers,
    /// non-increasing per query) and then discarded, because ranking is authoritative and scores
    /// are not comparable across entrants. A query written as an empty run maps to an empty list;
    /// a query absent from the file is absent from the dictionary.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file is empty, or a line is malformed — wrong field count, a second field other than
    /// <c>Q0</c>, a non-numeric or non-positive rank, a rank gap or repeat, a duplicate document,
    /// a score that is not an invariant-culture number or that rises against the ranks, a run tag
    /// that changes mid-file, or an <see cref="EmptyRunDocumentId"/> marker beside real documents.
    /// Every failure names the file and line. Skipping the line instead would yield a silently
    /// different metric.
    /// </exception>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(string runFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);

        var states = new Dictionary<string, QueryRun>(StringComparer.Ordinal);
        string? runTag = null;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(runFilePath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            runTag = ApplyLine(states, runTag, line, runFilePath, lineNumber);
        }

        if (states.Count == 0)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' contains no run lines. An empty run file scores zero on every " +
                "judged query, which reads as total retrieval failure rather than as a harness " +
                "that wrote nothing.");
        }

        var runs = new Dictionary<string, IReadOnlyList<string>>(states.Count, StringComparer.Ordinal);
        foreach (var (queryId, state) in states)
        {
            runs[queryId] = state.DocumentIds;
        }

        return runs;
    }

    private static void AppendQuery(
        StringBuilder content, string queryId, IReadOnlyList<ScoredDocument> documents, string runTag)
    {
        RequireToken(queryId, "query id", nameof(queryId));
        if (documents is null)
        {
            throw new ArgumentException($"Query '{queryId}' maps to a null ranking.", nameof(documents));
        }

        if (documents.Count == 0)
        {
            AppendLine(content, queryId, EmptyRunDocumentId, 1, 0, runTag);
            return;
        }

        var seen = new HashSet<string>(documents.Count, StringComparer.Ordinal);
        var previousScore = double.PositiveInfinity;
        for (var rank = 1; rank <= documents.Count; rank++)
        {
            var document = documents[rank - 1]
                ?? throw new ArgumentException($"Query '{queryId}' rank {rank} is null.", nameof(documents));

            RequireWritableDocument(queryId, document, rank, previousScore, seen);
            previousScore = document.Score;
            AppendLine(content, queryId, document.DocumentId, rank, document.Score, runTag);
        }
    }

    private static void RequireWritableDocument(
        string queryId, ScoredDocument document, int rank, double previousScore, HashSet<string> seen)
    {
        RequireToken(document.DocumentId, $"document id at '{queryId}' rank {rank}", nameof(document));
        if (string.Equals(document.DocumentId, EmptyRunDocumentId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Query '{queryId}' rank {rank} is '{EmptyRunDocumentId}', which is reserved to " +
                "mark a query that retrieved nothing and cannot name a real document.",
                nameof(document));
        }

        if (!double.IsFinite(document.Score))
        {
            throw new ArgumentException(
                $"Query '{queryId}' rank {rank} has score " +
                $"{document.Score.ToString(CultureInfo.InvariantCulture)}, which is not a finite " +
                "number.",
                nameof(document));
        }

        if (document.Score > previousScore)
        {
            throw new ArgumentException(
                $"Query '{queryId}' rank {rank} scores " +
                $"{document.Score.ToString(CultureInfo.InvariantCulture)}, above the rank before " +
                "it. Ranking is authoritative, but trec_eval re-sorts by score, so a file whose " +
                "scores rise where its ranks fall would be scored as two different rankings " +
                "depending on who reads it.",
                nameof(document));
        }

        if (!seen.Add(document.DocumentId))
        {
            throw new ArgumentException(
                $"Query '{queryId}' ranks document '{document.DocumentId}' twice. IrMetrics would " +
                "award its gain twice; DocumentRanking dedupes, so a duplicate means the entrant " +
                "bypassed document-level pooling.",
                nameof(document));
        }
    }

    private static void AppendLine(
        StringBuilder content, string queryId, string documentId, int rank, double score, string runTag) =>
        _ = content.Append(queryId).Append(' ').Append(IterationLiteral).Append(' ')
            .Append(documentId).Append(' ')
            .Append(rank.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(score.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(runTag).Append('\n');

    private static void RequireToken(string? value, string what, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                throw new ArgumentException(
                    $"The {what} '{value}' contains whitespace, which the whitespace-separated " +
                    "TREC format cannot carry: every reader, trec_eval included, would see the " +
                    "wrong field count on every line.",
                    parameterName);
            }
        }
    }

    private static string ApplyLine(
        Dictionary<string, QueryRun> states, string? runTag, string line, string runFilePath, int lineNumber)
    {
        var fields = line.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} has {fields.Length} whitespace-separated " +
                "fields; TREC run lines have exactly six: query id, the literal Q0, document id, " +
                "rank, score, run tag. Skipping the line instead would drop a ranked document and " +
                "silently change the metric.");
        }

        if (!string.Equals(fields[1], IterationLiteral, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} has '{fields[1]}' where the literal " +
                $"'{IterationLiteral}' belongs, so the columns are not what this reader expects — " +
                "a qrels file, whose third column is a judgement, is the classic wrong file here.");
        }

        var rank = ParseRank(fields[3], runFilePath, lineNumber);
        var score = ParseScore(fields[4], runFilePath, lineNumber);

        if (runTag is not null && !string.Equals(runTag, fields[5], StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} carries run tag '{fields[5]}' but the file " +
                $"began with '{runTag}'. Two tags in one file is the signature of two concatenated " +
                "run files — two entrants' rankings about to be scored as one.");
        }

        Apply(states, fields[0], fields[2], rank, score, runFilePath, lineNumber);
        return fields[5];
    }

    private static void Apply(
        Dictionary<string, QueryRun> states, string queryId, string documentId, int rank, double score,
        string runFilePath, int lineNumber)
    {
        if (!states.TryGetValue(queryId, out var state))
        {
            state = new QueryRun();
            states[queryId] = state;
        }

        if (state.IsEmpty)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} adds '{documentId}' to query '{queryId}', " +
                $"which an earlier line declared an empty run via '{EmptyRunDocumentId}'. The " +
                "file contradicts itself about whether this query retrieved anything.");
        }

        var expectedRank = state.DocumentIds.Count + 1;
        if (rank != expectedRank)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} claims rank {rank} for query '{queryId}', " +
                $"whose next rank is {expectedRank}. Ranks must be contiguous from 1: this " +
                "reader's output is a list whose position is the rank, so a gap would silently " +
                "renumber every document after it — and a gap almost always means lines were " +
                "filtered or lost after ranking, which is a different ranking, not a sparser one.");
        }

        if (string.Equals(documentId, EmptyRunDocumentId, StringComparison.Ordinal))
        {
            RequireLoneMarker(state, queryId, runFilePath, lineNumber);
            state.IsEmpty = true;
            return;
        }

        if (score > state.PreviousScore)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} scores '{documentId}' above the rank before " +
                $"it in query '{queryId}'. Rank order is authoritative here, but trec_eval " +
                "re-sorts by score, so this file would mean two different rankings depending on " +
                "who reads it.");
        }

        if (!state.Seen.Add(documentId))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} ranks document '{documentId}' twice for " +
                $"query '{queryId}'. IrMetrics would award its gain at both ranks.");
        }

        state.PreviousScore = score;
        state.DocumentIds.Add(documentId);
    }

    private static void RequireLoneMarker(QueryRun state, string queryId, string runFilePath, int lineNumber)
    {
        if (state.DocumentIds.Count > 0)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} declares query '{queryId}' an empty run via " +
                $"'{EmptyRunDocumentId}', but earlier lines ranked real documents for it. The " +
                "marker means 'retrieved nothing' and cannot share a query with a ranking.");
        }
    }

    private static int ParseRank(string field, string runFilePath, int lineNumber)
    {
        if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} has rank '{field}', which is not an integer.");
        }

        if (rank <= 0)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} has rank {rank}; TREC ranks are 1-based. A " +
                "rank of 0 usually means the writer emitted its zero-based loop index, which " +
                "shifts every rank in the file by one.");
        }

        return rank;
    }

    private static double ParseScore(string field, string runFilePath, int lineNumber)
    {
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' line {lineNumber} has score '{field}', which is not an " +
                "invariant-culture number. A comma decimal separator — '0,5' — is the classic " +
                "cause: a writer that used its machine's culture instead of the invariant one.");
        }

        return score;
    }

    /// <summary>One query's rankings as read so far, plus what the next line must satisfy.</summary>
    private sealed class QueryRun
    {
        public List<string> DocumentIds { get; } = [];

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public double PreviousScore { get; set; } = double.PositiveInfinity;

        public bool IsEmpty { get; set; }
    }
}
