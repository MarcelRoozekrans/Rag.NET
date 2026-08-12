using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// A content-addressed store of GraphRAG's entity-extraction responses, keyed on <b>the model that
/// produced them and the exact prompt it was sent</b>.
/// <para>
/// <b>The cached text is the experiment</b>, exactly as it is for <see cref="HypotheticalCache"/>.
/// Hosted LLMs are not bit-deterministic even at temperature 0, so "regenerate and compare" does
/// not exist: the graph the GraphRAG guard asserts against — its entities, its relationships, the
/// communities the clusterer finds in them — means one thing only if every extraction comes from one
/// generation run, done once by a tool, and read back verbatim ever after. That is why
/// <see cref="GraphExtractionCacheMode.RefuseOnMiss"/> exists and is the mode the guard runs in.
/// </para>
/// <para>
/// <b>Two components in the key, and the second one subsumes what the sibling cache spends three
/// on.</b> <see cref="HypotheticalCache"/> keys on the model identity, the prompt template, the
/// query and the hypothesis index, because it renders the prompt itself and can therefore key on
/// the parts. Here the prompt arrives already rendered from
/// <c>GraphEntityExtractionBehavior</c> — the extraction template with <c>{entity_types}</c>,
/// <c>{relationship_types}</c> and <c>{text}</c> substituted, or the gleaning template with
/// <c>{text}</c> and <c>{previous}</c> substituted — so the template, the type constraints and the
/// chunk text are all inside it verbatim. Editing any of them changes the rendered prompt, changes
/// the key, and misses. Adding them separately would key the same fact twice.
/// </para>
/// <para>
/// <b>What is <i>not</i> in the key is the model identity's job.</b> The rendered prompt says
/// nothing about which model saw it or at what temperature, and those move the output as surely as
/// the prompt does — see <see cref="GraphExtractionModelIdentity"/>.
/// </para>
/// <para>
/// <b>Gleaning replays too, and it replays only because the first call did.</b>
/// <c>GraphRagOptions.GleaningPasses</c> sends a follow-up whose prompt embeds the previous
/// extraction as JSON, so the second key is a function of the first response. Replay reproduces the
/// first response byte for byte, serialises it through the same options, and lands on the same
/// second key. A cache that served a <i>different</i> first response — a second generation run's,
/// say — would miss on the follow-up rather than quietly blending the two, which is the property
/// that makes a mixed cache loud instead of silent.
/// </para>
/// <para>
/// Entries live under <see cref="BeirDatasetCache.CacheDirectoryVariable"/> beside the datasets,
/// <b>never in the repository</b>. They are text derived from MultiHop-RAG's articles, which are
/// ODC-By and not ours to redistribute, and they are LLM output paid for per token — neither of
/// which belongs in git history.
/// </para>
/// </summary>
public sealed class GraphExtractionCache
{
    /// <summary>The subdirectory of the BEIR cache root that entries are written into.</summary>
    public const string DirectoryName = "graph-extractions";

    /// <summary>
    /// The eight bytes every entry starts with, read as one little-endian integer. A file that does
    /// not start with these is not one of ours and is treated as a miss rather than parsed.
    /// </summary>
    private static readonly ulong Magic = BinaryPrimitives.ReadUInt64LittleEndian("RAGNETX1"u8);

    private const int MagicLength = sizeof(ulong);

    /// <summary>The SHA-256 the entry is keyed by, stored so a wrong file cannot pass for a right one.</summary>
    private const int KeyDigestLength = 32;

    /// <summary>
    /// A SHA-256 over the text itself. A damaged extraction has no shape to fail by: it is JSON in
    /// a fenced block, and a truncated one either parses into a smaller graph or fails to parse and
    /// is swallowed by <c>GraphEntityExtractionBehavior</c>'s <c>JsonException</c> handler, which
    /// yields an empty extraction for that chunk and carries on. Both are silent, so integrity is
    /// checked here or nowhere.
    /// </summary>
    private const int ContentDigestLength = 32;

    /// <summary>Magic, then the key digest, then the content digest, then the text's byte count.</summary>
    private const int HeaderLength = MagicLength + KeyDigestLength + ContentDigestLength + sizeof(int);

    private readonly string _directory;
    private readonly string _modelIdentity;
    private readonly GraphExtractionCacheMode _mode;
    private long _hits;
    private long _misses;

    /// <summary>Creates a cache under <paramref name="cacheRootDirectory"/>.</summary>
    /// <param name="cacheRootDirectory">
    /// The BEIR cache root, normally <see cref="BeirDatasetCache.ResolveCacheDirectoryFromEnvironment"/>.
    /// Entries go in its <see cref="DirectoryName"/> subdirectory.
    /// </param>
    /// <param name="modelIdentity">
    /// What produced the text: model id and temperature, from
    /// <see cref="GraphExtractionModelIdentity.For"/>. Hashed into every key, so two identities
    /// never share an entry.
    /// </param>
    /// <param name="mode">
    /// What a miss does. The generation tool opens with <see cref="GraphExtractionCacheMode.Fill"/>;
    /// the guard opens with <see cref="GraphExtractionCacheMode.RefuseOnMiss"/>.
    /// </param>
    public GraphExtractionCache(
        string cacheRootDirectory,
        string modelIdentity,
        GraphExtractionCacheMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);

        _directory = Path.Combine(cacheRootDirectory, DirectoryName);
        _modelIdentity = modelIdentity;
        _mode = mode;
    }

    /// <summary>Gets the directory entries are stored in.</summary>
    public string EntryDirectory => _directory;

    /// <summary>Gets the model identity every key in this cache is salted with.</summary>
    public string ModelIdentity => _modelIdentity;

    /// <summary>Gets what this cache does on a miss.</summary>
    public GraphExtractionCacheMode Mode => _mode;

    /// <summary>Gets how many responses have been served from disk.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Gets how many responses were not on disk when asked for.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Returns whether a complete, intact entry exists for a prompt — the check that makes the
    /// generation run resumable.
    /// </summary>
    /// <param name="prompt">The rendered prompt, exactly as it would be sent.</param>
    /// <returns><see langword="true"/> when the response is on disk and intact.</returns>
    /// <remarks>
    /// Validates the entry rather than testing that a file exists, so a truncated or corrupt
    /// leftover reads as absent and a resumed run regenerates it instead of skipping past it.
    /// </remarks>
    public bool Contains(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return TryRead(ComputeKey(prompt)) is not null;
    }

    /// <summary>
    /// Returns the stored response for a prompt. On a miss,
    /// <see cref="GraphExtractionCacheMode.Fill"/> invokes <paramref name="generateAsync"/> and
    /// stores what it returns; <see cref="GraphExtractionCacheMode.RefuseOnMiss"/> throws without
    /// invoking anything.
    /// </summary>
    /// <param name="prompt">The rendered prompt. The whole of the key, apart from the identity.</param>
    /// <param name="generateAsync">
    /// Calls the model on a fill-mode miss. Never invoked on a hit, and never invoked at all in
    /// refuse-on-miss mode.
    /// </param>
    /// <param name="cancellationToken">Cancels the generation call.</param>
    /// <returns>The response text, exactly as first generated.</returns>
    /// <exception cref="InvalidOperationException">
    /// A miss in refuse-on-miss mode, naming the missing key — or a fill-mode call returning blank
    /// text, which parses to no entities at all and would be cached as a permanent empty answer for
    /// that chunk.
    /// </exception>
    public async Task<string> GetOrAddAsync(
        string prompt,
        Func<CancellationToken, Task<string>> generateAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(generateAsync);

        var key = ComputeKey(prompt);
        var cached = TryRead(key);
        if (cached is not null)
        {
            _ = Interlocked.Increment(ref _hits);
            return cached;
        }

        _ = Interlocked.Increment(ref _misses);
        if (_mode == GraphExtractionCacheMode.RefuseOnMiss)
        {
            throw MissingEntry(key, prompt);
        }

        var text = await generateAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"The model returned blank text for prompt key {key}. Blank parses to no entities " +
                "and no relationships, which is indistinguishable from a chunk that genuinely " +
                "contains none — so it is refused here, where the prompt is still on hand, rather " +
                "than cached as a permanent empty answer.");
        }

        await WriteAsync(key, text, cancellationToken).ConfigureAwait(false);
        return text;
    }

    /// <summary>The refuse-on-miss failure, naming the key so the gap is findable and fillable.</summary>
    /// <remarks>
    /// The prompt's first line goes in too. The key alone identifies the file but says nothing about
    /// <i>which</i> call is missing, and the two failures a reader must tell apart — "the generation
    /// run never covered this document" and "the chunking changed under the cache, so every key is
    /// new" — look identical without it.
    /// </remarks>
    private InvalidOperationException MissingEntry(string key, string prompt) =>
        new(
            $"No cached GraphRAG extraction {key} under model \"{_modelIdentity}\", for a prompt " +
            $"starting: {FirstLine(prompt)}\n" +
            $"Entries live in {_directory}. This cache was opened refuse-on-miss because the " +
            "cached text is the experiment: calling the model here would blend one generation run " +
            "with another inside a single graph, with nothing to say so. Run the extraction " +
            "generation tool to fill the cache, then re-run. If the cache looks full and this is " +
            "the FIRST prompt to miss, the prompt itself changed — the extraction template, the " +
            "configured entity or relationship types, or the chunking that produced the text — and " +
            "every key in the run is new.");

    /// <summary>The prompt's first line, truncated, for a failure message.</summary>
    private static string FirstLine(string prompt)
    {
        var end = prompt.IndexOf('\n');
        var line = end < 0 ? prompt : prompt[..end];
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    /// <summary>
    /// The entry key: SHA-256 over the model identity and the rendered prompt, lower-case hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each string is written as its byte length, then its bytes, then a NUL —
    /// <see cref="EmbeddingCache"/>'s construction, and <see cref="HypotheticalCache"/>'s. The
    /// length prefix is what makes the identity unambiguous: without it, identity <c>"ab"</c> with
    /// prompt <c>"c"</c> and identity <c>"a"</c> with prompt <c>"bc"</c> hash the same bytes, and a
    /// boundary bug is exactly the cross-generation reuse this key exists to prevent.
    /// </para>
    /// <para>
    /// SHA-256 for collision resistance over thousands of prompts, not as a security boundary:
    /// nothing here is adversarial.
    /// </para>
    /// </remarks>
    private string ComputeKey(string prompt)
    {
        var identity = Encoding.UTF8.GetBytes(_modelIdentity);
        var promptBytes = Encoding.UTF8.GetBytes(prompt);

        var buffer = new byte[(2 * (sizeof(int) + 1)) + identity.Length + promptBytes.Length];
        var offset = AppendField(buffer, 0, identity);
        _ = AppendField(buffer, offset, promptBytes);

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
        Path.Combine(_directory, key[..2], key + ".gex");

    /// <summary>
    /// Reads an entry, or <see langword="null"/> when there is not a complete and intact one.
    /// </summary>
    /// <remarks>
    /// <b>Anything unexpected is a miss, never a partial answer.</b> An interrupted generation run
    /// leaves truncated files, and a truncated extraction returned as if whole is a smaller graph
    /// that nothing anywhere reports as smaller. So the magic, the key digest, the length and the
    /// content digest all have to agree before the bytes are believed.
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
    /// The move is what keeps an interrupted generation run — and it will be interrupted, it is
    /// thousands of calls — from leaving a half-written entry where the next run would read it.
    /// <see cref="TryRead"/> refuses truncated files anyway; the two together are belt and braces.
    /// The move goes through <see cref="PublishRename"/> because on Windows an on-access scanner
    /// briefly holding the just-written partial denies the rename.
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
