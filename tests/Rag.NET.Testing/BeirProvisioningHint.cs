namespace Rag.NET.Testing;

/// <summary>
/// Names the conventional provisioning script when it exists, for skip messages whose variables
/// that script sets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Three separate sessions recorded this machine as unprovisioned while the
/// corpus sat at <c>~/.cache/ragnet-beir</c> with an <c>env.sh</c> beside it — the third time after
/// a note saying two sessions had already done it. The skip was correct every time; the message was
/// a dead end. Sourcing that script takes <c>Rag.NET.Embeddings.Onnx.Tests</c> from 10 skips to 0.
/// </para>
/// <para>
/// <b>Only for gates on variables the script actually sets</b> —
/// <c>RAGNET_BEIR_CACHE</c>, <c>RAGNET_ONNX_EMBED_MODEL</c>, <c>RAGNET_ONNX_EMBED_VOCAB</c>,
/// <c>RAGNET_ONNX_RERANK_MODEL</c> and <c>RAGNET_ONNX_RERANK_VOCAB</c>. Several other suites skip on
/// Whisper, Tesseract and Azure Document Intelligence settings that <c>env.sh</c> does not provision,
/// and telling those readers to source it would be a false claim. That distinction is the whole
/// reason the guard keys on the variable list rather than on the shape of the sentence.
/// </para>
/// <para>
/// <b>Shared rather than duplicated, reversing an earlier call.</b> Phase 6.2.42 duplicated this
/// into two test classes, reasoning that one sentence did not justify coupling unrelated projects.
/// That was right at two copies. At five call sites across four projects it stopped being right,
/// and nothing kept the copies in step — so it moved here, to the library those projects already use
/// for shared test infrastructure.
/// </para>
/// <para>
/// <b>Reports; never resolves.</b> This only ever describes what it found. Silently adopting a
/// directory the caller did not name would turn a visible skip into an invisible hour of
/// re-embedding against a corpus nobody asked for.
/// </para>
/// </remarks>
public static class BeirProvisioningHint
{
    /// <summary>The directory the provisioning script conventionally lives in.</summary>
    public const string ConventionalDirectoryName = "ragnet-beir";

    /// <summary>Describes the provisioning script when it is present on this machine.</summary>
    /// <returns>
    /// A sentence to append to a skip message, beginning with a space, or
    /// <see cref="string.Empty"/> when no script is there to name.
    /// </returns>
    /// <remarks>
    /// Returns empty rather than null so call sites can concatenate unconditionally, which keeps
    /// the composed message byte-identical to the original when there is nothing to add.
    /// </remarks>
    public static string Describe()
    {
        var envScript = ScriptPath();

        return File.Exists(envScript)
            ? $" '{envScript}' exists on this machine and sets these variables: source it."
            : string.Empty;
    }

    /// <summary>Gets the conventional path of the provisioning script, whether or not it exists.</summary>
    /// <returns>The absolute path.</returns>
    public static string ScriptPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        ConventionalDirectoryName,
        "env.sh");
}
