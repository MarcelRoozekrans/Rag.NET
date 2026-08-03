using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// A content-addressed store of HyDE hypothetical documents, keyed on <b>the model that generated
/// them, the prompt template it was given, the query, and the hypothesis index</b>.
/// <para>
/// The cached text is not an optimisation — <b>it is the experiment</b>. Hosted LLMs are not
/// bit-deterministic even at temperature 0, so "regenerate and compare" does not exist: the only
/// way the HyDE row of the ablation table means one thing is for every hypothetical to come from
/// one generation run, done once by a local tool, and read back verbatim ever after. That is why
/// <see cref="HypotheticalCacheMode.RefuseOnMiss"/> exists and is the mode the table runs in.
/// </para>
/// <para>
/// <b>All four components are in the key, and that is the whole design.</b> A key without the
/// prompt template keeps returning text generated from an instruction that has since been edited;
/// a key without the model keeps returning the previous model's prose. Neither throws, every test
/// stays green, and every number downstream is quietly wrong — the exact shape of failure
/// <see cref="EmbeddingCache"/> exists to prevent for vectors.
/// </para>
/// <para>
/// Entries live under <see cref="BeirDatasetCache.CacheDirectoryVariable"/> beside the datasets,
/// <b>never in the repository</b>. They are text derived from BEIR queries, and those corpora are
/// not ours to redistribute — FiQA in particular names no licence and restricts commercial use in
/// upstream's own words — so nothing derived from them is committed either.
/// </para>
/// </summary>
public sealed class HypotheticalCache
{
    /// <summary>The subdirectory of the BEIR cache root that entries are written into.</summary>
    public const string DirectoryName = "hypotheticals";

    /// <summary>
    /// The eight bytes every entry starts with, read as one little-endian integer. A file that does
    /// not start with these is not one of ours and is treated as a miss rather than parsed.
    /// </summary>
    private static readonly ulong Magic = BinaryPrimitives.ReadUInt64LittleEndian("RAGNETH1"u8);

    private const int MagicLength = sizeof(ulong);

    /// <summary>The SHA-256 the entry is keyed by, stored so a wrong file cannot pass for a right one.</summary>
    private const int KeyDigestLength = 32;

    /// <summary>
    /// A SHA-256 over the text itself. <see cref="EmbeddingCache"/> gets by without one because a
    /// damaged vector at least has the wrong dimension; damaged text has no shape to fail by — it
    /// decodes, embeds and searches like any other string — so its integrity has to be checked
    /// here or nowhere.
    /// </summary>
    private const int ContentDigestLength = 32;

    /// <summary>Magic, then the key digest, then the content digest, then the text's byte count.</summary>
    private const int HeaderLength = MagicLength + KeyDigestLength + ContentDigestLength + sizeof(int);

    private readonly string _directory;
    private readonly string _modelIdentity;
    private readonly string _promptTemplate;
    private readonly HypotheticalCacheMode _mode;
    private long _hits;
    private long _misses;

    /// <summary>Creates a cache under <paramref name="cacheRootDirectory"/>.</summary>
    /// <param name="cacheRootDirectory">
    /// The BEIR cache root, normally <see cref="BeirDatasetCache.ResolveCacheDirectoryFromEnvironment"/>.
    /// Entries go in its <see cref="DirectoryName"/> subdirectory.
    /// </param>
    /// <param name="modelIdentity">
    /// What generated the text: model id and revision, and anything else that changes it — the
    /// temperature, the provider. Hashed into every key, so two identities never share an entry.
    /// </param>
    /// <param name="promptTemplate">
    /// The exact instruction the model was given. Hashed into every key so that editing the prompt
    /// misses, rather than silently reusing hypotheticals generated from a different instruction.
    /// </param>
    /// <param name="mode">
    /// What a miss does. The generation tool opens with <see cref="HypotheticalCacheMode.Fill"/>;
    /// the table run opens with <see cref="HypotheticalCacheMode.RefuseOnMiss"/>.
    /// </param>
    public HypotheticalCache(
        string cacheRootDirectory,
        string modelIdentity,
        string promptTemplate,
        HypotheticalCacheMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptTemplate);

        _directory = Path.Combine(cacheRootDirectory, DirectoryName);
        _modelIdentity = modelIdentity;
        _promptTemplate = promptTemplate;
        _mode = mode;
    }

    /// <summary>Gets the directory entries are stored in.</summary>
    public string EntryDirectory => _directory;

    /// <summary>Gets the model identity every key in this cache is salted with.</summary>
    public string ModelIdentity => _modelIdentity;

    /// <summary>Gets the prompt template every key in this cache is salted with.</summary>
    public string PromptTemplate => _promptTemplate;

    /// <summary>Gets what this cache does on a miss.</summary>
    public HypotheticalCacheMode Mode => _mode;

    /// <summary>Gets how many hypotheticals have been served from disk.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Gets how many hypotheticals were not on disk when asked for.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Returns whether a complete, intact entry exists for the key — the check that makes the
    /// generation run resumable across its 7,062 calls.
    /// </summary>
    /// <remarks>
    /// This validates the entry rather than testing that a file exists, so a truncated or corrupt
    /// leftover reads as absent and a resumed run regenerates it instead of skipping past it.
    /// </remarks>
    /// <param name="query">The query the hypothetical answers.</param>
    /// <param name="hypothesisIndex">Which of the query's hypotheticals, zero-based.</param>
    public bool Contains(string query, int hypothesisIndex)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(hypothesisIndex);

        return TryRead(ComputeKey(query, hypothesisIndex)) is not null;
    }

    /// <summary>
    /// Returns the stored hypothetical for the key. On a miss, <see cref="HypotheticalCacheMode.Fill"/>
    /// invokes <paramref name="generateAsync"/> and stores its text;
    /// <see cref="HypotheticalCacheMode.RefuseOnMiss"/> throws without invoking anything.
    /// </summary>
    /// <param name="query">The query the hypothetical answers. Part of the key.</param>
    /// <param name="hypothesisIndex">Which of the query's hypotheticals, zero-based. Part of the key.</param>
    /// <param name="generateAsync">
    /// Generates the text on a fill-mode miss. Never invoked on a hit, and never invoked at all in
    /// refuse-on-miss mode.
    /// </param>
    /// <param name="cancellationToken">Cancels the generation call.</param>
    /// <returns>The hypothetical's text, exactly as first generated.</returns>
    /// <exception cref="InvalidOperationException">
    /// A miss in refuse-on-miss mode, naming the missing key — or a fill-mode generator returning
    /// blank text, which would embed to a meaningless vector and search like a real one.
    /// </exception>
    public async Task<string> GetOrAddAsync(
        string query,
        int hypothesisIndex,
        Func<CancellationToken, Task<string>> generateAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(hypothesisIndex);
        ArgumentNullException.ThrowIfNull(generateAsync);

        var key = ComputeKey(query, hypothesisIndex);
        var cached = TryRead(key);
        if (cached is not null)
        {
            _ = Interlocked.Increment(ref _hits);
            return cached;
        }

        _ = Interlocked.Increment(ref _misses);
        if (_mode == HypotheticalCacheMode.RefuseOnMiss)
        {
            throw MissingEntry(key, query, hypothesisIndex);
        }

        var text = await generateAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"The generator returned blank text for hypothesis {hypothesisIndex} of query " +
                $"\"{query}\". A blank hypothetical embeds to a meaningless vector and searches " +
                "like a real one, so it is refused here, where the model and query are still on " +
                "hand, rather than cached.");
        }

        await WriteAsync(key, text, cancellationToken).ConfigureAwait(false);
        return text;
    }

    /// <summary>The refuse-on-miss failure, naming the key so the gap is findable and fillable.</summary>
    private InvalidOperationException MissingEntry(string key, string query, int hypothesisIndex) =>
        new(
            $"No cached hypothetical {key} — hypothesis {hypothesisIndex} of query \"{query}\" " +
            $"under model \"{_modelIdentity}\". This cache was opened refuse-on-miss because the " +
            "cached text is the experiment: regenerating here would blend two generation runs " +
            "into one table with nothing to say so. Run the hypothetical generation tool to fill " +
            "the cache, then re-run.");

    /// <summary>
    /// The entry key: SHA-256 over the model identity, the prompt template, the query and the
    /// hypothesis index, lower-case hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each string is written as its byte length, then its bytes, then a NUL, and the index goes
    /// last as four fixed bytes — <see cref="EmbeddingCache"/>'s construction, extended to four
    /// components. The length prefixes are what make the identity unambiguous: without them,
    /// model <c>"ab"</c> with prompt <c>"c"</c> and model <c>"a"</c> with prompt <c>"bc"</c> would
    /// hash the same bytes, and a boundary bug is exactly the cross-generation reuse this key
    /// exists to prevent. With four components there are three such boundaries, not one.
    /// </para>
    /// <para>
    /// SHA-256 for collision resistance over thousands of queries, not as a security boundary:
    /// nothing here is adversarial.
    /// </para>
    /// </remarks>
    private string ComputeKey(string query, int hypothesisIndex)
    {
        var identity = Encoding.UTF8.GetBytes(_modelIdentity);
        var prompt = Encoding.UTF8.GetBytes(_promptTemplate);
        var queryBytes = Encoding.UTF8.GetBytes(query);

        var buffer = new byte[
            (3 * (sizeof(int) + 1)) + identity.Length + prompt.Length + queryBytes.Length + sizeof(int)];
        var offset = AppendField(buffer, 0, identity);
        offset = AppendField(buffer, offset, prompt);
        offset = AppendField(buffer, offset, queryBytes);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), hypothesisIndex);

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>Writes one length-prefixed, NUL-terminated field and returns the next offset.</summary>
    private static int AppendField(byte[] buffer, int offset, byte[] field)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), field.Length);
        field.CopyTo(buffer.AsSpan(offset + sizeof(int)));
        buffer[offset + sizeof(int) + field.Length] = 0;
        return offset + sizeof(int) + field.Length + 1;
    }

    /// <summary>Gets the file an entry lives in, sharded so no directory holds every entry.</summary>
    private string PathFor(string key) =>
        Path.Combine(_directory, key[..2], key + ".hyp");

    /// <summary>
    /// Reads an entry, or <see langword="null"/> when there is not a complete and intact one.
    /// </summary>
    /// <remarks>
    /// <b>Anything unexpected is a miss, never a partial answer.</b> An interrupted generation run
    /// leaves truncated files, and a truncated hypothetical returned as if whole is silent wrong
    /// data in the one row of the table built on this text. So the magic, the key digest, the
    /// length and the content digest all have to agree before the bytes are believed.
    /// </remarks>
    private string? TryRead(string key)
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
    private static string? TryParse(byte[] bytes, string key)
    {
        if (bytes.Length < HeaderLength || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != Magic)
        {
            return null;
        }

        var storedKey = Convert.ToHexStringLower(bytes.AsSpan(MagicLength, KeyDigestLength));
        if (!string.Equals(storedKey, key, StringComparison.Ordinal))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(MagicLength + KeyDigestLength + ContentDigestLength));
        if (length <= 0 || bytes.Length != HeaderLength + length)
        {
            return null;
        }

        var content = bytes.AsSpan(HeaderLength, length);
        var storedDigest = bytes.AsSpan(MagicLength + KeyDigestLength, ContentDigestLength);
        Span<byte> digest = stackalloc byte[ContentDigestLength];
        _ = SHA256.HashData(content, digest);

        // Chosen for its ReadOnlySpan signature, not its timing property — nothing here is
        // adversarial, and two 32-byte digests compare in nanoseconds either way.
        if (!CryptographicOperations.FixedTimeEquals(digest, storedDigest))
        {
            return null;
        }

        return Encoding.UTF8.GetString(content);
    }

    /// <summary>
    /// Writes an entry, via a uniquely named temporary file that is moved into place.
    /// </summary>
    /// <remarks>
    /// The move is what keeps the interrupted generation run — and it will be interrupted, it is
    /// 7,062 calls — from leaving a half-written entry where the next run would read it.
    /// <see cref="TryRead"/> refuses truncated files anyway; the two together are belt and braces,
    /// and the belt is cheap. The move goes through <see cref="PublishRename"/> because on Windows
    /// an on-access scanner briefly holding the just-written partial — or a just-written
    /// destination being replaced — denies the rename; the measurements are on that class.
    /// </remarks>
    private async Task WriteAsync(string key, string text, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var content = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[HeaderLength + content.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Magic);
        Convert.FromHexString(key).CopyTo(bytes.AsSpan(MagicLength));
        _ = SHA256.HashData(content, bytes.AsSpan(MagicLength + KeyDigestLength, ContentDigestLength));
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(MagicLength + KeyDigestLength + ContentDigestLength), content.Length);
        content.CopyTo(bytes.AsSpan(HeaderLength));

        var partialPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial";
        File.WriteAllBytes(partialPath, bytes);
        await PublishRename.ReplaceFileAsync(partialPath, path, cancellationToken).ConfigureAwait(false);
    }
}
