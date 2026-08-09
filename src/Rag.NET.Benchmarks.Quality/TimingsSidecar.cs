using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The cost half of the Phase 5.1 boundary: beside every <see cref="TrecRunFile"/> an entrant
/// writes, it writes one <c>&lt;run-file&gt;.timings.json</c> holding what it measured about
/// itself — indexing wall-clock, one raw latency per judged query, and the counts that say what
/// those numbers mean. The .NET side reads every entrant's sidecar and computes every percentile
/// itself, so no library's own idea of "p99" reaches a published table.
/// <para>
/// <b>The path is derived, never passed in.</b> A caller free to name the sidecar could pair
/// SciFact's run file with FiQA's timings and get a row where every number is plausible and every
/// number is wrong. <see cref="PathFor"/> is the only way to name one.
/// </para>
/// <para>
/// <b>The pairing is checked, not assumed.</b> A shared path prefix says the two files sit
/// together, not that they describe the same run, so <see cref="ReadPaired"/> makes them agree on
/// the run tag and on the exact set of query ids — and refuses a missing sidecar rather than
/// skipping, because an opted-in cost measurement that skipped when its data was absent would read
/// as a pass.
/// </para>
/// <para>
/// <b>Named fields, not positions.</b> Unlike the TREC run file, this format is ours and is read
/// only by us, so it is JSON: a positional format silently misreads when a field is added or
/// reordered, and this file's whole purpose is to be trusted about which number is which.
/// </para>
/// <para>
/// <b>Every malformed or missing field throws</b>, naming the file — the same stance
/// <see cref="TrecRunFile"/> takes on a malformed line, for the same reason. The alternative to
/// failing is defaulting, and the defaults here are all catastrophically quiet: a missing latency
/// read as 0 ms is a p50 that reads as a win, and a missing miss count read as 0 asserts a warm
/// embedding cache for a run that was cold. An unknown field is refused too, because the likeliest
/// unknown field is a percentile an entrant computed for itself.
/// </para>
/// <para>
/// Numbers are written and parsed under <see cref="CultureInfo.InvariantCulture"/>, lines end in
/// <c>\n</c>, fields are in a fixed order and query ids in ordinal order, so the same measurements
/// produce a byte-identical file on any machine. The stakes are higher than in the run file: the
/// development machine's culture writes <c>12,5</c> for twelve and a half, and in JSON that is not
/// a mangled number but two members — the failure would surface as a parse error in whoever read
/// the file next, far from whoever wrote it.
/// </para>
/// </summary>
public static class TimingsSidecar
{
    /// <summary>
    /// What <see cref="PathFor"/> appends to a run file's path. Appended rather than substituted
    /// for the extension so the run file's own name stays visible in the sidecar's — a directory
    /// of these is read by people as well as by tests.
    /// </summary>
    public const string FileSuffix = ".timings.json";

    private const string RunTagField = "runTag";
    private const string IndexingSecondsField = "indexingSeconds";
    private const string EmbeddingCacheHitsField = "embeddingCacheHits";
    private const string EmbeddingCacheMissesField = "embeddingCacheMisses";
    private const string UnitCountField = "unitCount";
    private const string MaxUnitsPerDocumentField = "maxUnitsPerDocument";
    private const string QueryLatenciesField = "queryLatenciesMilliseconds";

    /// <summary>Every field this reader understands, in the order <see cref="Write"/> emits them.</summary>
    private static readonly string[] KnownFields =
    [
        RunTagField,
        IndexingSecondsField,
        EmbeddingCacheHitsField,
        EmbeddingCacheMissesField,
        UnitCountField,
        MaxUnitsPerDocumentField,
        QueryLatenciesField,
    ];

    /// <summary>Gets the sidecar path belonging to a run file — its first run's, see the overload.</summary>
    /// <param name="runFilePath">The run file the timings were measured alongside.</param>
    /// <returns>
    /// The run file's path with <see cref="FileSuffix"/> appended. Derived rather than supplied,
    /// so a run file and a sidecar cannot be paired by accident — see the class remarks.
    /// </returns>
    public static string PathFor(string runFilePath) => PathFor(runFilePath, runIndex: 1);

    /// <summary>Gets the sidecar path belonging to one repeat run of a run file.</summary>
    /// <param name="runFilePath">The run file the timings were measured alongside.</param>
    /// <param name="runIndex">
    /// Which repeat this is, 1-based. Repeats exist because no cost figure may be published from a
    /// single run — <see cref="CostReproducibility"/> compares them — and without an index every
    /// repeat would overwrite the last, leaving the gate one sidecar where it needs at least two.
    /// </param>
    /// <returns>
    /// For run 1, the historical unindexed name — <c>&lt;run-file&gt;.timings.json</c> — so a
    /// single-run pairing stays exactly where every existing reader looks. For run <c>N ≥ 2</c>,
    /// <c>&lt;run-file&gt;.timings.N.json</c>: the same convention extended with the index before
    /// the extension, never a second format, so the family shares the run file's visible name and
    /// sorts together in a directory listing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="runIndex"/> is below 1 — "run 0" would silently alias run 1 under any
    /// convention that maps both onto a real path.
    /// </exception>
    public static string PathFor(string runFilePath, int runIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(runIndex, 1);
        return runIndex == 1
            ? runFilePath + FileSuffix
            : runFilePath + ".timings." + runIndex.ToString(CultureInfo.InvariantCulture) + ".json";
    }

    /// <summary>
    /// Writes one entrant's self-measured timings beside its run file, validating that the result
    /// is a file <see cref="Read"/> will agree about.
    /// </summary>
    /// <param name="runFilePath">
    /// The run file these timings belong to. The sidecar goes at <see cref="PathFor"/> of it,
    /// overwritten if present; the run file itself is neither read nor written here.
    /// </param>
    /// <param name="timings">What the entrant measured about itself.</param>
    /// <exception cref="ArgumentException">
    /// The latency map is empty (an entrant that timed zero queries lost its query set, exactly as
    /// <see cref="TrecRunFile.Write"/> reads an empty run set), a latency or the indexing time is
    /// negative or non-finite, a count is negative, the unit counts are not positive, or the run
    /// tag or a query id carries whitespace or a character JSON would have to escape.
    /// </exception>
    public static void Write(string runFilePath, EntrantTimings timings) =>
        Write(runFilePath, timings, runIndex: 1);

    /// <summary>
    /// Writes one repeat run's self-measured timings beside its run file — <see cref="Write(string, EntrantTimings)"/>
    /// with the sidecar at <see cref="PathFor(string, int)"/> of <paramref name="runIndex"/>, so
    /// repeat runs accumulate instead of overwriting and <see cref="CostReproducibility"/> has
    /// something to compare.
    /// </summary>
    /// <param name="runFilePath">
    /// The run file these timings belong to; the run file itself is neither read nor written here.
    /// </param>
    /// <param name="timings">What the entrant measured about itself on this repeat.</param>
    /// <param name="runIndex">Which repeat this is, 1-based — see <see cref="PathFor(string, int)"/>.</param>
    /// <exception cref="ArgumentException">
    /// The timings are unwritable, exactly as <see cref="Write(string, EntrantTimings)"/> refuses them.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="runIndex"/> is below 1.</exception>
    public static void Write(string runFilePath, EntrantTimings timings, int runIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);
        ArgumentNullException.ThrowIfNull(timings);
        RequireWritableRun(timings);
        RequireWritableLatencies(timings.QueryLatenciesMilliseconds);

        var queryIds = new string[timings.QueryLatenciesMilliseconds.Count];
        var next = 0;
        foreach (var queryId in timings.QueryLatenciesMilliseconds.Keys)
        {
            queryIds[next++] = queryId;
        }

        // Ordinal order, so the same measurements produce a byte-identical file everywhere.
        Array.Sort(queryIds, StringComparer.Ordinal);

        var content = new StringBuilder("{\n");
        AppendText(content, RunTagField, timings.RunTag);
        AppendNumber(content, IndexingSecondsField, Number(timings.IndexingSeconds));
        AppendNumber(content, EmbeddingCacheHitsField, Number(timings.EmbeddingCacheHits));
        AppendNumber(content, EmbeddingCacheMissesField, Number(timings.EmbeddingCacheMisses));
        AppendNumber(content, UnitCountField, Number(timings.UnitCount));
        AppendNumber(content, MaxUnitsPerDocumentField, Number(timings.MaxUnitsPerDocument));
        AppendLatencies(content, queryIds, timings.QueryLatenciesMilliseconds);

        File.WriteAllText(PathFor(runFilePath, runIndex), content.ToString());
    }

    /// <summary>Reads the sidecar beside a run file back to the samples the entrant measured.</summary>
    /// <param name="runFilePath">The run file whose sidecar to read.</param>
    /// <returns>
    /// The entrant's timings, with the per-query latencies keyed by query id so the set of ids
    /// stays available to check against the run file's.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The sidecar is not a JSON object, is missing a field, carries a field this reader does not
    /// understand, repeats a field, has an empty latency map, or holds a value of the wrong kind
    /// or outside its range — a latency that is not a non-negative finite number, a count that is
    /// not a non-negative integer, or a run tag or query id carrying whitespace. Every failure
    /// names the file, and a latency failure names the query, because the alternative to failing
    /// is a quietly different percentile.
    /// </exception>
    public static EntrantTimings Read(string runFilePath) => Read(runFilePath, runIndex: 1);

    /// <summary>
    /// Reads one repeat run's sidecar back — <see cref="Read(string)"/> against the sidecar at
    /// <see cref="PathFor(string, int)"/> of <paramref name="runIndex"/>.
    /// </summary>
    /// <param name="runFilePath">The run file whose repeat sidecar to read.</param>
    /// <param name="runIndex">Which repeat to read, 1-based — see <see cref="PathFor(string, int)"/>.</param>
    /// <returns>That repeat's timings, as <see cref="Read(string)"/> returns them.</returns>
    /// <exception cref="InvalidDataException">
    /// The sidecar is malformed, in any of the ways <see cref="Read(string)"/> refuses.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="runIndex"/> is below 1.</exception>
    public static EntrantTimings Read(string runFilePath, int runIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);

        var sidecarPath = PathFor(runFilePath, runIndex);
        using var document = Parse(File.ReadAllText(sidecarPath), sidecarPath);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' holds a JSON {root.ValueKind} where the timings object belongs. " +
                "One sidecar describes one entrant on one dataset; an array is the signature of " +
                "several runs concatenated into one file.");
        }

        RequireOnlyKnownFieldsOnce(root, sidecarPath);

        return new EntrantTimings(
            ReadToken(root, RunTagField, sidecarPath),
            ReadSeconds(root, IndexingSecondsField, sidecarPath),
            ReadLatencies(root, sidecarPath),
            ReadCount(root, EmbeddingCacheHitsField, sidecarPath, minimum: 0),
            ReadCount(root, EmbeddingCacheMissesField, sidecarPath, minimum: 0),
            ReadCount(root, UnitCountField, sidecarPath, minimum: 1),
            ReadCount(root, MaxUnitsPerDocumentField, sidecarPath, minimum: 1));
    }

    /// <summary>
    /// Reads the sidecar beside a run file <b>and proves the two describe the same run</b> — same
    /// entrant, same queries.
    /// <para>
    /// A run file and a sidecar that disagree are worse than a missing sidecar, because they look
    /// fine: every cell is populated, and the latencies belong to another entrant, another dataset
    /// or another query split. Both halves of the check are things only the pair can show —
    /// neither file is self-evidently wrong on its own.
    /// </para>
    /// </summary>
    /// <param name="runFilePath">
    /// The run file. It is read in full by <see cref="TrecRunFile.Read"/> — the same reader every
    /// published quality figure goes through — so a pair this accepts is a pair that scores.
    /// </param>
    /// <returns>The entrant's timings, checked against the run file beside them.</returns>
    /// <exception cref="FileNotFoundException">
    /// The run file is absent (nothing ran), or the run file is present and its sidecar is not.
    /// <b>Absent is a failure, not a skip</b> — the <c>refuse-on-miss</c> rule the opted-in BEIR
    /// cases already apply to a missing run file. An opted-in cost measurement that skipped when
    /// the sidecar was gone would read as a pass.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The run file is malformed, the two disagree about the run tag, or they disagree about the
    /// set of query ids.
    /// </exception>
    public static EntrantTimings ReadPaired(string runFilePath) =>
        ReadPaired(runFilePath, runIndex: 1);

    /// <summary>
    /// Reads one repeat run's sidecar and proves it describes the run file beside it —
    /// <see cref="ReadPaired(string)"/> against the sidecar at <see cref="PathFor(string, int)"/>
    /// of <paramref name="runIndex"/>. Every repeat gets the full pairing check, not just the
    /// first: a repeat whose sidecar came from another entrant is exactly as wrong as a first run
    /// whose sidecar did.
    /// </summary>
    /// <param name="runFilePath">The run file, read in full by <see cref="TrecRunFile.Read"/>.</param>
    /// <param name="runIndex">Which repeat to read, 1-based — see <see cref="PathFor(string, int)"/>.</param>
    /// <returns>That repeat's timings, checked against the run file beside them.</returns>
    /// <exception cref="FileNotFoundException">
    /// The run file is absent, or the repeat's sidecar is — a failure and not a skip, exactly as
    /// <see cref="ReadPaired(string)"/> refuses, naming the indexed path so the message says which
    /// repeat is missing.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The run file is malformed, or the pair disagree about the run tag or the query ids.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="runIndex"/> is below 1.</exception>
    public static EntrantTimings ReadPaired(string runFilePath, int runIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);

        // The run file first: if neither file exists, the entrant never ran, and naming the
        // sidecar would send a reader looking for a timing bug in a run that does not exist.
        var runs = TrecRunFile.Read(runFilePath);
        var runTag = ReadRunTag(runFilePath);

        var sidecarPath = PathFor(runFilePath, runIndex);
        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException(
                $"'{runFilePath}' exists but '{sidecarPath}' does not, so the entrant ran and " +
                "emitted no timings. This is a failure and not a skip — the refuse-on-miss rule " +
                "the opted-in BEIR cases already apply to a missing run file — because an " +
                "opted-in cost measurement that skipped when its data was absent would read as a " +
                "pass. Re-run the entrant that produced the run file; it writes both.",
                sidecarPath);
        }

        var timings = Read(runFilePath, runIndex);
        RequireSameRunTag(runTag, timings.RunTag, runFilePath, sidecarPath);
        RequireSameQueries(runs, timings.QueryLatenciesMilliseconds, runFilePath, sidecarPath);
        return timings;
    }

    /// <summary>
    /// The run file's tag, from its first ranked line. Reading one line is enough because
    /// <see cref="TrecRunFile.Read"/> has already refused any file whose tag changes partway
    /// through — so by here there is only one tag to find.
    /// </summary>
    private static string ReadRunTag(string runFilePath)
    {
        foreach (var line in File.ReadLines(runFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields[^1];
        }

        throw new InvalidDataException(
            $"'{runFilePath}' has no run lines, so it carries no run tag to check the sidecar " +
            "against.");
    }

    private static void RequireSameRunTag(
        string runFileTag, string sidecarTag, string runFilePath, string sidecarPath)
    {
        if (!string.Equals(runFileTag, sidecarTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{runFilePath}' is tagged '{runFileTag}' but '{sidecarPath}' beside it is " +
                $"tagged '{sidecarTag}'. This is the pairing error that produces a complete, " +
                "plausible, wrong row: one library's latencies published under another's name, " +
                "with nothing missing and nothing to notice.");
        }
    }

    private static void RequireSameQueries(
        IReadOnlyDictionary<string, IReadOnlyList<string>> runs,
        IReadOnlyDictionary<string, double> latencies,
        string runFilePath,
        string sidecarPath)
    {
        var ranked = Missing(runs.Keys, latencies.ContainsKey);
        if (ranked.Count > 0)
        {
            throw new InvalidDataException(
                $"'{runFilePath}' answers {Describe(ranked)} that '{sidecarPath}' does not time. " +
                "A sidecar short of a query yields a percentile over a subset, and it reads as a " +
                "faster entrant whenever the untimed queries were the slow ones — which is " +
                "exactly what to expect when an entrant stops timing partway through. A query " +
                "that retrieved nothing still cost time and still needs a sample.");
        }

        var timed = Missing(latencies.Keys, runs.ContainsKey);
        if (timed.Count > 0)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' times {Describe(timed)} that '{runFilePath}' never answers. " +
                "The sidecar is measuring a query set this run file does not have — a leftover " +
                "from another dataset or another qrels split.");
        }
    }

    /// <summary>The ids not present on the other side, in ordinal order so the message is stable.</summary>
    private static List<string> Missing(IEnumerable<string> queryIds, Func<string, bool> isPresent)
    {
        var missing = new List<string>();
        foreach (var queryId in queryIds)
        {
            if (!isPresent(queryId))
            {
                missing.Add(queryId);
            }
        }

        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    /// <summary>
    /// The count with the first few ids named. Bounded because a wholly mismatched pair differs by
    /// every query, and a message listing 1,406 ids says less than one listing three and a count.
    /// </summary>
    private static string Describe(List<string> queryIds)
    {
        const int Shown = 3;
        var named = string.Join("', '", queryIds.GetRange(0, Math.Min(Shown, queryIds.Count)));
        var more = queryIds.Count > Shown
            ? $" and {(queryIds.Count - Shown).ToString(CultureInfo.InvariantCulture)} more"
            : "";
        var counted = queryIds.Count == 1
            ? "1 query"
            : $"{queryIds.Count.ToString(CultureInfo.InvariantCulture)} queries";

        return $"{counted} — '{named}'{more} —";
    }

    // ------------------------------------------------------------------------------- writing

    private static void RequireWritableRun(EntrantTimings timings)
    {
        RequireToken(timings.RunTag, "run tag", nameof(timings));
        RequireMeasurable(timings.IndexingSeconds, "indexing wall-clock", nameof(timings));
        RequireAtLeast(timings.EmbeddingCacheHits, 0, "embedding-cache hit count", nameof(timings));
        RequireAtLeast(timings.EmbeddingCacheMisses, 0, "embedding-cache miss count", nameof(timings));
        RequireAtLeast(timings.UnitCount, 1, "unit count", nameof(timings));
        RequireAtLeast(timings.MaxUnitsPerDocument, 1, "max units per document", nameof(timings));
    }

    private static void RequireWritableLatencies(IReadOnlyDictionary<string, double> latencies)
    {
        ArgumentNullException.ThrowIfNull(latencies);
        if (latencies.Count == 0)
        {
            throw new ArgumentException(
                "The per-query latency map is empty. An entrant that timed zero queries did not " +
                "run infinitely fast; it lost its query set, and every percentile over nothing is " +
                "0/0 — which would either throw far from here or become a zero-millisecond row, " +
                "the best-looking row in the table.",
                nameof(latencies));
        }

        foreach (var (queryId, latency) in latencies)
        {
            RequireToken(queryId, "query id", nameof(latencies));
            RequireMeasurable(latency, $"latency for query '{queryId}'", nameof(latencies));
        }
    }

    private static void RequireToken(string? value, string what, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '"' or '\\' || char.IsControl(character))
            {
                throw new ArgumentException(
                    $"The {what} '{value}' contains a character this file cannot carry unescaped. " +
                    "It is refused rather than escaped: the same token has to appear in the " +
                    "whitespace-separated run file this sidecar is paired against, so an id that " +
                    "only JSON can represent could never be matched to a ranking anyway.",
                    parameterName);
            }
        }
    }

    private static void RequireMeasurable(double value, string what, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException(
                $"The {what} is {value.ToString(CultureInfo.InvariantCulture)}, which is not a " +
                "non-negative finite number of milliseconds or seconds. No clock runs backwards, " +
                "so a negative span is a subtraction the wrong way round; and a NaN does not " +
                "compare, so it would sort anywhere and quietly become whichever percentile the " +
                "sort left it at.",
                parameterName);
        }
    }

    private static void RequireAtLeast(int value, int minimum, string what, string parameterName)
    {
        if (value < minimum)
        {
            throw new ArgumentException(
                $"The {what} is {value.ToString(CultureInfo.InvariantCulture)}, below the minimum " +
                $"of {minimum.ToString(CultureInfo.InvariantCulture)}. A run that indexed nothing " +
                "would post the fastest indexing time in the table, and a retrieval depth " +
                "multiplied by zero is no retrieval at all.",
                parameterName);
        }
    }

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AppendText(StringBuilder content, string field, string value) =>
        _ = content.Append("  \"").Append(field).Append("\": \"").Append(value).Append("\",\n");

    private static void AppendNumber(StringBuilder content, string field, string value) =>
        _ = content.Append("  \"").Append(field).Append("\": ").Append(value).Append(",\n");

    private static void AppendLatencies(
        StringBuilder content, string[] queryIds, IReadOnlyDictionary<string, double> latencies)
    {
        _ = content.Append("  \"").Append(QueryLatenciesField).Append("\": {\n");
        for (var i = 0; i < queryIds.Length; i++)
        {
            _ = content.Append("    \"").Append(queryIds[i]).Append("\": ")
                .Append(Number(latencies[queryIds[i]]))
                .Append(i == queryIds.Length - 1 ? "\n" : ",\n");
        }

        _ = content.Append("  }\n}\n");
    }

    // ------------------------------------------------------------------------------- reading

    private static JsonDocument Parse(string json, string sidecarPath)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // JsonException counts lines from zero; this file's readers count from one, as
            // TrecRunFile's errors do.
            var line = (ex.LineNumber ?? 0) + 1;
            throw new InvalidDataException(
                $"'{sidecarPath}' is not valid JSON at line " +
                $"{line.ToString(CultureInfo.InvariantCulture)}: {ex.Message} A decimal comma — " +
                "'12,5' for twelve and a half — is the classic cause, and it does not merely " +
                "misparse: JSON reads it as two members, so a writer using its machine's culture " +
                "instead of the invariant one produces a file that fails here rather than there.",
                ex);
        }
    }

    private static void RequireOnlyKnownFieldsOnce(JsonElement root, string sidecarPath)
    {
        var seen = new HashSet<string>(KnownFields.Length, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (Array.IndexOf(KnownFields, field.Name) < 0)
            {
                throw new InvalidDataException(
                    $"'{sidecarPath}' carries a field this reader does not understand, " +
                    $"'{field.Name}'. It is refused rather than ignored: the likeliest extra " +
                    "field is a percentile the entrant computed for itself, and letting five " +
                    "entrants' own definitions of p50 and p99 into one table is exactly what " +
                    "computing them here, once, from the raw samples exists to prevent.");
            }

            if (!seen.Add(field.Name))
            {
                throw new InvalidDataException(
                    $"'{sidecarPath}' carries the field '{field.Name}' twice. JSON readers " +
                    "disagree about which one wins, so two entrants reading this file could " +
                    "legitimately report two different numbers from it.");
            }
        }
    }

    private static JsonElement RequireField(JsonElement root, string field, string sidecarPath)
    {
        if (!root.TryGetProperty(field, out var value))
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has no '{field}' field. Every field is required, because the " +
                "only alternative to failing here is defaulting: a missing latency read as 0 ms " +
                "is a p50 that reads as a win, and a missing embedding-cache miss count read as 0 " +
                "asserts a warm cache for a run that may have been cold — which is the one thing " +
                "that changes what the indexing figure means.");
        }

        return value;
    }

    private static string ReadToken(JsonElement root, string field, string sidecarPath)
    {
        var value = RequireField(root, field, sidecarPath);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has a JSON {value.ValueKind} in '{field}' where a string belongs.");
        }

        var text = value.GetString() ?? "";
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || character is '"' or '\\' || char.IsControl(character))
            {
                throw new InvalidDataException(
                    $"'{sidecarPath}' has '{text}' in '{field}', which carries a character the " +
                    "whitespace-separated run file this sidecar pairs against cannot hold — so it " +
                    "could never be matched to the run file's own tag.");
            }
        }

        if (text.Length == 0)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has an empty '{field}'. An untagged sidecar cannot be shown to " +
                "belong to the run file beside it.");
        }

        return text;
    }

    private static double ReadSeconds(JsonElement root, string field, string sidecarPath)
    {
        var value = RequireField(root, field, sidecarPath);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds)
            || !double.IsFinite(seconds) || seconds < 0)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has '{value}' in '{field}', which is not a non-negative finite " +
                "number. Wall-clock does not run backwards, so a negative span means the " +
                "subtraction was the wrong way round or the clock moved under the measurement.");
        }

        return seconds;
    }

    private static int ReadCount(JsonElement root, string field, string sidecarPath, int minimum)
    {
        var value = RequireField(root, field, sidecarPath);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count))
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has '{value}' in '{field}', which is not a 32-bit integer. A " +
                "fractional count is not a rounding artefact — it is a field holding something " +
                "other than a count, and truncating it would hide whatever put it there.");
        }

        if (count < minimum)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has {count.ToString(CultureInfo.InvariantCulture)} in " +
                $"'{field}', below the minimum of {minimum.ToString(CultureInfo.InvariantCulture)}. " +
                "A run that indexed nothing would post the fastest indexing time in the table.");
        }

        return count;
    }

    private static IReadOnlyDictionary<string, double> ReadLatencies(JsonElement root, string sidecarPath)
    {
        var value = RequireField(root, QueryLatenciesField, sidecarPath);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has a JSON {value.ValueKind} in '{QueryLatenciesField}' where " +
                "an object keyed by query id belongs. The keys are what the run file's query ids " +
                "are checked against; a bare array of samples could not be checked at all.");
        }

        var latencies = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in value.EnumerateObject())
        {
            var latency = ReadLatency(sample, sidecarPath);
            if (!latencies.TryAdd(sample.Name, latency))
            {
                throw new InvalidDataException(
                    $"'{sidecarPath}' times query '{sample.Name}' twice in " +
                    $"'{QueryLatenciesField}'. One of the two samples would be discarded, and " +
                    "which one is up to the reader.");
            }
        }

        if (latencies.Count == 0)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' times no queries at all. An entrant that timed zero queries " +
                "lost its query set rather than running infinitely fast, and every percentile " +
                "over an empty sample set is 0/0.");
        }

        return latencies;
    }

    private static double ReadLatency(JsonProperty sample, string sidecarPath)
    {
        if (sample.Value.ValueKind != JsonValueKind.Number || !sample.Value.TryGetDouble(out var latency)
            || !double.IsFinite(latency) || latency < 0)
        {
            throw new InvalidDataException(
                $"'{sidecarPath}' has '{sample.Value}' as the latency of query '{sample.Name}', " +
                "which is not a non-negative finite number of milliseconds. A NaN is the worst " +
                "of these: it does not compare, so it sorts anywhere and silently becomes " +
                "whichever percentile the sort leaves it at.");
        }

        return latency;
    }
}
