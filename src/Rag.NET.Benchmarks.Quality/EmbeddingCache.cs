using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// A content-addressed store of embedding vectors, keyed on <b>the model that produced them and the
/// exact text embedded</b>, so a corpus is embedded once and re-scored for free.
/// <para>
/// Not an optimisation. SciFact's 5,183 documents take ~355 s; FiQA is roughly an order of magnitude
/// larger, and this milestone runs two protocols per dataset before the ablation table multiplies
/// that across rows. Without a cache the table costs hours per run, and a benchmark nobody re-runs
/// stops being a measurement.
/// </para>
/// <para>
/// <b>The model identity is in the key, and that is the whole design.</b> A cache keyed on text
/// alone keeps returning vectors from the previous model after the model changes: nothing throws,
/// every test stays green, and every number downstream is quietly wrong — which is the exact shape
/// of failure this milestone keeps finding. Changing the model, its revision or its
/// <c>max_seq_length</c> must change the identity string handed to the constructor.
/// </para>
/// <para>
/// Entries live under <see cref="BeirDatasetCache.CacheDirectoryVariable"/> beside the datasets,
/// <b>never in the repository</b>. They are derived data for a corpus that is itself not vendored,
/// they are megabytes per dataset, and a checked-in vector is a vector nobody re-derives.
/// </para>
/// <para>
/// <b>Timed measurements must not read this cache from disk.</b> One entry read costs ~10 ms with
/// a cold OS page cache and ~0.7 ms with a hot one, and one read per text inside an indexing span
/// made identical comparison runs differ by up to 23x on run order alone — a figure about the
/// page cache, not about any library. <see cref="Prefetch"/> reads every vector a run will need
/// into memory before any span starts; after it, <see cref="GetOrAddAsync"/> answers from that
/// memory only and refuses texts the prefetch never saw, so no timed span can touch disk or an
/// embedder by construction.
/// </para>
/// </summary>
public sealed class EmbeddingCache
{
    /// <summary>The subdirectory of the BEIR cache root that entries are written into.</summary>
    public const string DirectoryName = "embeddings";

    /// <summary>
    /// The eight bytes every entry starts with, read as one little-endian integer. A file that does
    /// not start with these is not one of ours and is treated as a miss rather than parsed.
    /// </summary>
    private static readonly ulong Magic = BinaryPrimitives.ReadUInt64LittleEndian("RAGNETE1"u8);

    private const int MagicLength = sizeof(ulong);

    /// <summary>The SHA-256 the entry is keyed by, stored so a wrong file cannot pass for a right one.</summary>
    private const int DigestLength = 32;

    /// <summary>Magic, then the key digest, then the dimension.</summary>
    private const int HeaderLength = MagicLength + DigestLength + sizeof(int);

    private readonly string _directory;
    private readonly string _modelIdentity;
    private readonly ILogger<EmbeddingCache>? _logger;
    private Dictionary<string, float[]>? _prefetched;
    private long _hits;
    private long _misses;

    /// <summary>Creates a cache under <paramref name="cacheRootDirectory"/>.</summary>
    /// <param name="cacheRootDirectory">
    /// The BEIR cache root, normally <see cref="BeirDatasetCache.ResolveCacheDirectoryFromEnvironment"/>.
    /// Entries go in its <see cref="DirectoryName"/> subdirectory.
    /// </param>
    /// <param name="modelIdentity">
    /// What produced the vectors: model id and revision, and anything else that changes them — the
    /// sequence length, the pooling, the normalisation. It is hashed into every key, so two
    /// identities never share an entry and a stale identity is the one way this cache can lie.
    /// </param>
    /// <param name="logger">Optional.</param>
    public EmbeddingCache(
        string cacheRootDirectory, string modelIdentity, ILogger<EmbeddingCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);

        _directory = Path.Combine(cacheRootDirectory, DirectoryName);
        _modelIdentity = modelIdentity;
        _logger = logger;
    }

    /// <summary>Gets the directory entries are stored in.</summary>
    public string EntryDirectory => _directory;

    /// <summary>Gets the model identity every key in this cache is salted with.</summary>
    public string ModelIdentity => _modelIdentity;

    /// <summary>Gets how many texts have been served from disk.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Gets how many texts have had to be embedded.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Returns a vector for every text, embedding only the ones not already stored. After
    /// <see cref="Prefetch"/>, answers come from memory only — no disk, no embedder — and a text
    /// the prefetch never saw throws rather than quietly reading disk inside a timed span.
    /// </summary>
    /// <param name="texts">The texts, in the order vectors are wanted back in.</param>
    /// <param name="embedAsync">
    /// Embeds the texts that missed. Called at most once, with the distinct misses in order; it must
    /// return exactly one vector per text it was handed.
    /// </param>
    /// <param name="cancellationToken">Cancels the embedding call.</param>
    /// <returns>One vector per text, in the order the texts were given.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="embedAsync"/> returned a different number of vectors than it was asked for,
    /// or an empty one. Either would misalign every vector after it against its document, and a
    /// misaligned corpus scores like a retrieval defect rather than like a bug here.
    /// </exception>
    public async Task<IReadOnlyList<float[]>> GetOrAddAsync(
        IReadOnlyList<string> texts,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> embedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(embedAsync);

        if (_prefetched is not null)
        {
            return ServeFromMemory(_prefetched, texts);
        }

        var keys = new string[texts.Count];
        var results = new float[texts.Count][];
        var missingTexts = new List<string>();
        var missingKeys = new List<string>();
        var slotsByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var hitCount = 0;

        for (var i = 0; i < texts.Count; i++)
        {
            keys[i] = ComputeKey(texts[i]);
            var cached = TryRead(keys[i]);
            if (cached is not null)
            {
                results[i] = cached;
                hitCount++;
                _ = Interlocked.Increment(ref _hits);
                continue;
            }

            _ = Interlocked.Increment(ref _misses);
            Enqueue(slotsByKey, missingTexts, missingKeys, keys[i], texts[i], i);
        }

        if (missingTexts.Count > 0)
        {
            var embedded = await embedAsync(missingTexts, cancellationToken).ConfigureAwait(false);
            await StoreAsync(embedded, missingTexts, missingKeys, slotsByKey, results, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_logger is not null)
        {
            BeirLog.EmbeddingCacheBatch(_logger, _modelIdentity, hitCount, texts.Count - hitCount);
        }

        return results;
    }

    /// <summary>
    /// Reads every one of <paramref name="texts"/>' vectors from disk into memory, so every later
    /// <see cref="GetOrAddAsync"/> call is answered without touching disk or any embedder.
    /// </summary>
    /// <param name="texts">
    /// Every text the run will embed — corpus units <b>and</b> query texts, since query embedding
    /// also goes through this cache and sits inside the retrieval span.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// One or more texts have no entry: the cache is <b>cold</b> for this corpus. Timed spans
    /// serve vectors from memory and never embed, so embedding here would silently reintroduce
    /// the very cost — model inference and file I/O — the prefetch exists to exclude; warm the
    /// cache first (the parity measurement embeds the same texts through this cache) and re-run.
    /// </exception>
    /// <remarks>
    /// This is the only disk access a comparison run performs against the cache, and it runs
    /// before any timed span starts. The hit and miss counters move here, once per occurrence,
    /// exactly as a <see cref="GetOrAddAsync"/> lookup would have counted them — so a sidecar's
    /// counts still describe what the disk really held, not the in-memory serving.
    /// </remarks>
    public void Prefetch(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        _prefetched ??= new Dictionary<string, float[]>(StringComparer.Ordinal);
        var missing = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            var key = ComputeKey(texts[i]);
            if (_prefetched.ContainsKey(key))
            {
                _ = Interlocked.Increment(ref _hits);
                continue;
            }

            var cached = TryRead(key);
            if (cached is null)
            {
                _ = Interlocked.Increment(ref _misses);
                missing++;
                continue;
            }

            _prefetched[key] = cached;
            _ = Interlocked.Increment(ref _hits);
        }

        if (missing > 0)
        {
            throw new InvalidOperationException(
                $"{missing} of the {texts.Count} texts to prefetch have no entry under " +
                $"'{_directory}' (identity '{_modelIdentity}'): the cache is cold. Timed spans " +
                "serve vectors from memory and never embed — embedding here would reintroduce " +
                "the model and disk cost the prefetch exists to exclude — so warm the cache " +
                "first (the parity measurement embeds the same texts through this cache).");
        }
    }

    /// <summary>
    /// Answers one batch from the prefetched map, refusing any text the prefetch never saw —
    /// even one whose entry exists on disk, because a disk read inside a timed span is exactly
    /// what prefetching exists to exclude. Counters do not move here; they described the disk
    /// once, at prefetch time.
    /// </summary>
    private IReadOnlyList<float[]> ServeFromMemory(
        Dictionary<string, float[]> prefetched, IReadOnlyList<string> texts)
    {
        var results = new float[texts.Count][];
        var missing = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            if (prefetched.TryGetValue(ComputeKey(texts[i]), out var vector))
            {
                results[i] = vector;
                continue;
            }

            missing++;
        }

        if (missing > 0)
        {
            throw new InvalidOperationException(
                $"{missing} of the {texts.Count} texts in this batch were never prefetched. " +
                "Every text a timed span embeds must be covered by the prefetch; serving it " +
                "from disk or an embedder here would put I/O back inside the span.");
        }

        return results;
    }

    /// <summary>
    /// Records that <paramref name="key"/> has to be embedded, and which result slots want it.
    /// </summary>
    /// <remarks>
    /// A text repeated inside one batch is embedded once, not once per occurrence. BEIR corpora do
    /// contain exact duplicates, and paying for them twice is paying for the thing this class exists
    /// to avoid.
    /// </remarks>
    private static void Enqueue(
        Dictionary<string, List<int>> slotsByKey,
        List<string> missingTexts,
        List<string> missingKeys,
        string key,
        string text,
        int slot)
    {
        if (slotsByKey.TryGetValue(key, out var slots))
        {
            slots.Add(slot);
            return;
        }

        slotsByKey[key] = [slot];
        missingTexts.Add(text);
        missingKeys.Add(key);
    }

    /// <summary>Writes each newly embedded vector to disk and into its result slots.</summary>
    private async Task StoreAsync(
        IReadOnlyList<float[]> embedded,
        List<string> missingTexts,
        List<string> missingKeys,
        Dictionary<string, List<int>> slotsByKey,
        float[][] results,
        CancellationToken cancellationToken)
    {
        if (embedded is null || embedded.Count != missingTexts.Count)
        {
            throw new InvalidOperationException(
                $"The embedder was handed {missingTexts.Count} texts and returned " +
                $"{embedded?.Count ?? 0} vectors. Every vector after the first mismatch would be " +
                "stored against the wrong text, and a corpus embedded one place out scores like a " +
                "retrieval defect rather than like a bug here.");
        }

        for (var i = 0; i < embedded.Count; i++)
        {
            var vector = embedded[i];
            if (vector is null || vector.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The embedder returned an empty vector for text {i} of {embedded.Count}. An " +
                    "empty vector retrieves nothing and would be indistinguishable from a document " +
                    "the corpus never had.");
            }

            await WriteAsync(missingKeys[i], vector, cancellationToken).ConfigureAwait(false);
            foreach (ref readonly var slot in CollectionsMarshal.AsSpan(slotsByKey[missingKeys[i]]))
            {
                results[slot] = vector;
            }
        }
    }

    /// <summary>
    /// The entry key: SHA-256 over the model identity and the exact text, lower-case hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity and the text are separated by a NUL and the identity's length is written in
    /// front of it, so no pair of (identity, text) can be re-cut into a different pair with the same
    /// bytes. Without that, model <c>"a"</c> with text <c>"b:c"</c> and model <c>"a:b"</c> with text
    /// <c>"c"</c> could collide, which is a boundary bug that produces exactly the cross-model reuse
    /// this key exists to prevent.
    /// </para>
    /// <para>
    /// SHA-256 for collision resistance over a corpus of tens of thousands of texts, not as a
    /// security boundary: nothing here is adversarial.
    /// </para>
    /// </remarks>
    private string ComputeKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var identityBytes = Encoding.UTF8.GetBytes(_modelIdentity);
        var textBytes = Encoding.UTF8.GetBytes(text);

        var buffer = new byte[sizeof(int) + identityBytes.Length + 1 + textBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, identityBytes.Length);
        identityBytes.CopyTo(buffer.AsSpan(sizeof(int)));
        buffer[sizeof(int) + identityBytes.Length] = 0;
        textBytes.CopyTo(buffer.AsSpan(sizeof(int) + identityBytes.Length + 1));

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>Gets the file an entry lives in, sharded so no directory holds every entry.</summary>
    private string PathFor(string key) =>
        Path.Combine(_directory, key[..2], key + ".vec");

    /// <summary>
    /// Reads an entry, or <see langword="null"/> when there is not a complete and intact one.
    /// </summary>
    /// <remarks>
    /// <b>Anything unexpected is a miss, never a partial answer.</b> A run interrupted mid-write
    /// leaves a truncated file, and the cost of re-embedding one document is seconds against a
    /// benchmark number that is silently wrong. So the magic, the length, the digest and the
    /// dimension all have to agree before the bytes are believed — the digest in particular, because
    /// it is what makes a file that is intact but <i>wrong</i>, rather than merely short, still a
    /// miss.
    /// </remarks>
    private float[]? TryRead(string key)
    {
        var path = PathFor(key);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return TryParse(bytes, key);
    }

    /// <summary>Parses an entry's bytes, or <see langword="null"/> if anything does not agree.</summary>
    private static float[]? TryParse(byte[] bytes, string key)
    {
        if (bytes.Length < HeaderLength || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != Magic)
        {
            return null;
        }

        var storedDigest = Convert.ToHexStringLower(bytes.AsSpan(MagicLength, DigestLength));
        if (!string.Equals(storedDigest, key, StringComparison.Ordinal))
        {
            return null;
        }

        var dimension = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(MagicLength + DigestLength));
        if (dimension <= 0 || bytes.Length != HeaderLength + (dimension * sizeof(float)))
        {
            return null;
        }

        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(HeaderLength + (i * sizeof(float))));
        }

        return vector;
    }

    /// <summary>
    /// Writes an entry, via a uniquely named temporary file that is moved into place.
    /// </summary>
    /// <remarks>
    /// The move is what keeps an interrupted run from leaving a half-written entry where the next
    /// run would read it. <see cref="TryRead"/> refuses truncated files anyway — the two together
    /// are belt and braces, and the belt is cheap. The move goes through
    /// <see cref="PublishRename"/> because on Windows an on-access scanner briefly holding the
    /// just-written partial — or a just-written destination being replaced — denies the rename;
    /// the measurements are on that class.
    /// </remarks>
    private async Task WriteAsync(string key, float[] vector, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = new byte[HeaderLength + (vector.Length * sizeof(float))];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
        Convert.FromHexString(key).CopyTo(bytes.AsSpan(MagicLength));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(MagicLength + DigestLength), vector.Length);
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(HeaderLength + (i * sizeof(float))), vector[i]);
        }

        var partialPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial";
        File.WriteAllBytes(partialPath, bytes);
        await PublishRename.ReplaceFileAsync(partialPath, path, cancellationToken).ConfigureAwait(false);
    }
}
