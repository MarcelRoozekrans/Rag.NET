using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Search;

/// <summary>
/// Thread-safe in-memory BM25 inverted index.
/// Parameters: k1=1.5, b=0.75 (Lucene defaults).
/// </summary>
public sealed class InMemoryBm25Index : IBm25Index
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    private readonly Dictionary<string, List<(int docId, int tf)>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (TextChunk chunk, int length)> _docs = [];

    /// <summary>
    /// Document id → the internal doc ids indexed under it. Exists so <see cref="Remove"/> can
    /// answer "is this document present at all?" in O(1) instead of scanning <see cref="_docs"/>
    /// and every postings list. That matters because <c>StorageBehavior</c> calls
    /// <see cref="Remove"/> before every ingest, including first-time ingests that have nothing
    /// to remove: without the early exit, bulk-ingesting N documents costs O(N²).
    /// </summary>
    private readonly Dictionary<string, List<int>> _docIdsByDocument = new(StringComparer.Ordinal);

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SynonymMap? _synonymMap;

    public InMemoryBm25Index(SynonymMap? synonymMap = null)
    {
        _synonymMap = synonymMap;
    }

    public void Add(int docId, TextChunk chunk)
    {
        var tokens = Tokenize(chunk.Text, _synonymMap);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ref readonly var token in CollectionsMarshal.AsSpan(tokens))
            tf[token] = tf.TryGetValue(token, out var count) ? count + 1 : 1;

        _lock.EnterWriteLock();
        try
        {
            if (_docs.ContainsKey(docId))
                return; // caller must remove before re-adding
            _docs[docId] = (chunk, tokens.Count);

            var documentId = chunk.DocumentId.Value;
            if (!_docIdsByDocument.TryGetValue(documentId, out var docIds))
            {
                docIds = [];
                _docIdsByDocument[documentId] = docIds;
            }
            docIds.Add(docId);

            foreach (var (term, freq) in tf)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    list = [];
                    _postings[term] = list;
                }
                list.Add((docId, freq));
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes every chunk indexed under <paramref name="documentId"/>. Absent documents cost
    /// a single dictionary lookup — see <see cref="_docIdsByDocument"/> for why that early exit
    /// is load-bearing rather than a micro-optimisation.
    /// </summary>
    public void Remove(string documentId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_docIdsByDocument.TryGetValue(documentId, out var toRemove))
                return;

            _docIdsByDocument.Remove(documentId);

            foreach (ref readonly var docId in CollectionsMarshal.AsSpan(toRemove))
                _docs.Remove(docId);

            var toRemoveSet = new HashSet<int>(toRemove);
            foreach (var list in _postings.Values)
                list.RemoveAll(entry => toRemoveSet.Contains(entry.docId));

            var emptyTerms = new List<string>();
            foreach (var kv in _postings)
            {
                if (kv.Value.Count == 0)
                    emptyTerms.Add(kv.Key);
            }

            foreach (ref readonly var term in CollectionsMarshal.AsSpan(emptyTerms))
                _postings.Remove(term);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK)
    {
        var queryTokens = Tokenize(query, _synonymMap);
        if (queryTokens.Count == 0) return [];
        if (topK <= 0) return [];

        // Deduplicate query tokens before acquiring lock to avoid LINQ inside loop
        var uniqueTokens = new HashSet<string>(queryTokens, StringComparer.Ordinal);

        _lock.EnterReadLock();
        try
        {
            if (_docs.Count == 0) return [];

            double totalLength = 0;
            foreach (var d in _docs.Values)
                totalLength += d.length;
            var avgDocLen = totalLength / _docs.Count;
            var docCount = _docs.Count;
            var scores = new Dictionary<int, double>();

            foreach (var token in uniqueTokens)
            {
                if (!_postings.TryGetValue(token, out var postingList)) continue;

                var df = postingList.Count;
                var idf = Math.Log((docCount - df + 0.5) / (df + 0.5) + 1.0);

                foreach (var (docId, tf) in CollectionsMarshal.AsSpan(postingList))
                {
                    var docLen = _docs[docId].length;
                    var tfNorm = (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * docLen / avgDocLen));
                    scores[docId] = scores.TryGetValue(docId, out var s) ? s + idf * tfNorm : idf * tfNorm;
                }
            }

            var result = new List<(TextChunk chunk, double score)>(Math.Min(scores.Count, topK));
            foreach (var kv in scores)
                result.Add((_docs[kv.Key].chunk, kv.Value));

            result.Sort(static (a, b) => b.score.CompareTo(a.score));

            if (result.Count > topK)
                result.RemoveRange(topK, result.Count - topK);

            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try
        {
            _docs.Clear();
            _postings.Clear();
            _docIdsByDocument.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose() => _lock.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static List<string> Tokenize(string text, SynonymMap? synonymMap = null)
    {
        var baseTokens = ExtractBaseTokens(text);

        if (synonymMap is null) return baseTokens;

        // Pass 2: expand single base tokens.
        var tokens = new List<string>(baseTokens);
        foreach (ref readonly var token in CollectionsMarshal.AsSpan(baseTokens))
            foreach (var syn in synonymMap.Expand(token))
                tokens.AddRange(Tokenize(syn));

        // Pass 3: expand multi-word phrases formed by consecutive *base* tokens only.
        // The inner window is capped at MaxKeyTokenCount so the scan is
        // O(n * maxPhraseLen) rather than O(n²). When all keys are single-word
        // (MaxKeyTokenCount <= 1) this pass is skipped entirely.
        var maxPhraseLen = synonymMap.MaxKeyTokenCount;
        if (baseTokens.Count > 1 && maxPhraseLen > 1)
        {
            for (int i = 0; i < baseTokens.Count; i++)
                for (int len = 2; len <= maxPhraseLen && i + len <= baseTokens.Count; len++)
                {
                    var phrase = string.Join(" ", baseTokens.GetRange(i, len));
                    foreach (var syn in synonymMap.Expand(phrase))
                        tokens.AddRange(Tokenize(syn));
                }
        }

        return tokens;
    }

    /// <summary>
    /// Splits text into the terms BM25 counts: whitespace-delimited words, and character bigrams
    /// for scripts that do not delimit words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The word rule alone made this index inert for CJK (#299).</b> Splitting on runs of
    /// <see cref="char.IsLetterOrDigit(char)"/> is Unicode-aware and works for every script that
    /// puts spaces between words — Latin, Cyrillic, Greek, Arabic, Hebrew. Chinese, Japanese and
    /// Korean do not, so an entire sentence became ONE token:
    /// </para>
    /// <code>
    ///   "the quick brown fox"   -> 4 tokens
    ///   "人工智能是未来"          -> 1 token, the whole sentence
    /// </code>
    /// <para>
    /// One token per sentence makes term frequency meaningless: nothing matches short of an exact
    /// sentence repeat, so hybrid retrieval silently degraded to dense-only for those languages
    /// without erroring.
    /// </para>
    /// <para>
    /// <b>Overlapping bigrams, which is what Lucene's CJK analyzer does.</b> <c>人工智能</c> becomes
    /// <c>人工 工智 智能</c>. It needs no dictionary and no segmentation model — the cost is one
    /// extra term per character and some imprecision at word boundaries, against an index that
    /// previously matched nothing at all. A dictionary segmenter is better and is what the tokeniser
    /// seam in #299 would let someone plug in.
    /// </para>
    /// <para>
    /// <b>Nothing changes for text without CJK.</b> The ranges below appear in no Latin, Cyrillic,
    /// Greek, Arabic or Hebrew text, so every existing corpus tokenises exactly as before — which is
    /// why this is a default rather than an option.
    /// </para>
    /// </remarks>
    /// <param name="text">The text to split.</param>
    /// <returns>The terms, lower-cased.</returns>
    private static List<string> ExtractBaseTokens(string text)
    {
        var baseTokens = new List<string>();
        var lower = text.ToLowerInvariant();
        var start = -1;

        for (int i = 0; i <= lower.Length; i++)
        {
            var isCjk = i < lower.Length && IsCjk(lower[i]);
            var isAlnum = !isCjk && i < lower.Length && char.IsLetterOrDigit(lower[i]);

            if (isAlnum && start == -1)
            {
                start = i;
            }
            else if (!isAlnum && start != -1)
            {
                baseTokens.Add(lower[start..i]);
                start = -1;
            }

            // A CJK run ends the word token before it and contributes bigrams of its own.
            if (isCjk)
            {
                var runEnd = i;
                while (runEnd < lower.Length && IsCjk(lower[runEnd]))
                {
                    runEnd++;
                }

                AddCjkBigrams(baseTokens, lower.AsSpan(i, runEnd - i));
                i = runEnd - 1;
            }
        }

        return baseTokens;
    }

    /// <summary>Adds overlapping character bigrams, or the single character when the run is one.</summary>
    /// <param name="tokens">Receives the terms.</param>
    /// <param name="run">A run of CJK characters.</param>
    private static void AddCjkBigrams(List<string> tokens, ReadOnlySpan<char> run)
    {
        // A lone character still has to be searchable — a one-character run yields itself rather
        // than nothing, which is the edge case a naive bigram loop drops.
        if (run.Length == 1)
        {
            tokens.Add(run.ToString());
            return;
        }

        for (var i = 0; i + 1 < run.Length; i++)
        {
            tokens.Add(run.Slice(i, 2).ToString());
        }
    }

    /// <summary>Reports whether a character belongs to a script that does not delimit words.</summary>
    /// <remarks>
    /// CJK ideographs and their extension A, the compatibility block, Hiragana, Katakana, and Hangul
    /// syllables and Jamo. Deliberately does not include Thai, Khmer or Lao: those are also
    /// undelimited but bigramming them is not the accepted treatment, and claiming support this
    /// change has not tested would be worse than leaving them as they are.
    /// </remarks>
    /// <param name="c">The character to classify.</param>
    /// <returns>Whether the character is CJK.</returns>
    private static bool IsCjk(char c) =>
        (c >= '一' && c <= '鿿') ||     // CJK Unified Ideographs
        (c >= '㐀' && c <= '䶿') ||     // CJK Unified Ideographs Extension A
        (c >= '豈' && c <= '﫿') ||     // CJK Compatibility Ideographs
        (c >= '぀' && c <= 'ゟ') ||     // Hiragana
        (c >= '゠' && c <= 'ヿ') ||     // Katakana
        (c >= '가' && c <= '힯') ||     // Hangul Syllables
        (c >= 'ᄀ' && c <= 'ᇿ');       // Hangul Jamo
}
