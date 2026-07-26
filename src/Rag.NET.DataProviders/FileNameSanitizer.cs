using System.Buffers;

namespace Rag.NET.DataProviders;

/// <summary>
/// Turns user-controlled text (a mail subject, an issue title, a channel name) into a
/// file name that is safe to carry on a <c>FileHandle</c>, deterministically and
/// independently of the host operating system.
/// </summary>
/// <remarks>
/// <para>
/// The invalid-character set is <b>pinned</b> rather than taken from
/// <see cref="Path.GetInvalidFileNameChars"/>. That method returns 41 characters on Windows
/// but only <c>'\0'</c> and <c>'/'</c> on Linux and macOS, so the same message ingested on a
/// Linux host would keep <c>:</c> <c>*</c> <c>?</c> <c>"</c> <c>&lt;</c> <c>&gt;</c> <c>|</c>
/// and control characters in its name. It also clones a fresh array on every call. The set
/// used here is the Windows superset — <c>&lt; &gt; : " / \ | ? *</c> plus the C0 control
/// range U+0000–U+001F — cached in a <see cref="SearchValues{T}"/>.
/// </para>
/// <para>
/// Reserved Windows device names (<c>CON</c>, <c>PRN</c>, <c>NUL</c>, <c>AUX</c>,
/// <c>COM1</c>…<c>LPT9</c>) are <b>deliberately not</b> handled. This is not an oversight:
/// <c>FileHandle.FileName</c> is metadata used for parser selection and display, never a
/// filesystem path, so a handle named <c>NUL.md</c> is harmless. Callers that do write these
/// names to disk must handle device names themselves.
/// </para>
/// <para>
/// This type is <c>public</c> — unlike the sibling <c>FileExtensionMatcher</c> — because each
/// connector ships as its own assembly and NuGet package, and this assembly grants
/// <c>InternalsVisibleTo</c> to the test project only.
/// </para>
/// </remarks>
public static class FileNameSanitizer
{
    /// <summary>The character substituted for every invalid character.</summary>
    private const char Replacement = '_';

    /// <summary>Punctuation invalid in a Windows file name.</summary>
    private const string InvalidPunctuation = "<>:\"/\\|?*";

    /// <summary>Number of C0 control characters (U+0000–U+001F) in the invalid set.</summary>
    private const int ControlCharCount = 0x20;

    /// <summary>
    /// Last-resort name used when both the value and the caller's fallback sanitize away to
    /// nothing. Returning an empty string here would produce a bare extension (<c>".md"</c>),
    /// so a non-empty stem is guaranteed.
    /// </summary>
    private const string LastResort = "untitled";

    private static readonly SearchValues<char> InvalidChars = BuildInvalidChars();

    /// <summary>
    /// Sanitizes <paramref name="value"/> into a file-name stem. The extension, if any, is
    /// the caller's business and is not part of the input.
    /// </summary>
    /// <param name="value">The user-controlled text. May be <see langword="null"/>.</param>
    /// <param name="fallback">
    /// The name to use when <paramref name="value"/> is null, whitespace, or sanitizes away to
    /// nothing. It is sanitized by the same rules, so an id-derived fallback containing invalid
    /// characters is still safe.
    /// </param>
    /// <param name="maxLength">Maximum length of the returned stem. Must be positive.</param>
    /// <returns>A non-empty stem containing no invalid character, no leading or trailing
    /// whitespace and no trailing dot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is not positive.</exception>
    public static string Sanitize(string? value, string fallback, int maxLength = 128)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        var cleaned = Clean(value, maxLength);
        if (cleaned.Length > 0)
            return cleaned;

        var cleanedFallback = Clean(fallback, maxLength);
        return cleanedFallback.Length > 0 ? cleanedFallback : LastResort;
    }

    /// <summary>
    /// Applies the rules in order: replace invalid characters, trim surrounding whitespace,
    /// trim trailing dots (invalid on Windows), then truncate. Returns an empty string when
    /// nothing usable survives, which is the caller's signal to fall back.
    /// </summary>
    /// <remarks>
    /// Written with index arithmetic rather than <c>ReadOnlySpan</c> extension methods
    /// (<c>Trim</c>, <c>TrimEnd</c>, <c>ContainsAny</c>) because those take the span by value
    /// and trip the EPS06 hidden-struct-copy analyzer, which is an error in this repo.
    /// </remarks>
    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        bool substituted = IndexOfInvalid(value) >= 0;
        var  replaced    = substituted ? Replace(value) : value;

        int start = 0;
        int end   = replaced.Length;
        TrimEdges(replaced, ref start, ref end);

        if (end - start > maxLength)
        {
            end = start + maxLength;

            // Never split a surrogate pair: a lone high surrogate renders as U+FFFD.
            if (end > start && char.IsHighSurrogate(replaced[end - 1]))
                end--;

            // Truncation can re-expose a trailing dot or space.
            TrimEdges(replaced, ref start, ref end);
        }

        int length = end - start;
        if (length <= 0 || (substituted && IsAllReplacement(replaced, start, end)))
            return string.Empty;

        return length == replaced.Length ? replaced : replaced.Substring(start, length);
    }

    /// <summary>
    /// Reports whether nothing survived but the substitutions themselves. A name such as
    /// <c>"___"</c> produced from <c>"///"</c> is no more usable than an empty one, so it
    /// collapses to the caller's fallback.
    /// </summary>
    /// <remarks>
    /// The caller gates this on whether a substitution actually happened, so a title the user
    /// really did type as <c>"___"</c> is kept. Only underscores this type introduced count as
    /// "nothing survived".
    /// </remarks>
    private static bool IsAllReplacement(string value, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (value[i] != Replacement)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Narrows <paramref name="start"/>/<paramref name="end"/> past leading whitespace, then
    /// past trailing whitespace and trailing dots.
    /// </summary>
    /// <remarks>
    /// The trailing pass loops to a fixed point because the two conditions re-expose each
    /// other: stripping the dots from <c>"name. . ."</c> uncovers a space, stripping that
    /// space uncovers another dot, and so on. A single pass of each — or even two — leaves a
    /// trailing space, breaking the guarantee this type's <c>Sanitize</c> makes about its
    /// return value.
    /// </remarks>
    private static void TrimEdges(string value, ref int start, ref int end)
    {
        while (start < end && char.IsWhiteSpace(value[start])) start++;

        int previous;
        do
        {
            previous = end;
            while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
            while (end > start && value[end - 1] == '.') end--;
        }
        while (end != previous);
    }

    /// <summary>Substitutes every invalid character. The caller has already established that
    /// there is at least one.</summary>
    private static string Replace(string value) =>
        string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                var c = source[i];
                destination[i] = InvalidChars.Contains(c) ? Replacement : c;
            }
        });

    private static int IndexOfInvalid(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (InvalidChars.Contains(value[i]))
                return i;
        }

        return -1;
    }

    private static SearchValues<char> BuildInvalidChars()
    {
        Span<char> chars = stackalloc char[InvalidPunctuation.Length + ControlCharCount];
        InvalidPunctuation.CopyTo(chars);
        for (int i = 0; i < ControlCharCount; i++)
            chars[InvalidPunctuation.Length + i] = (char)i;

        return SearchValues.Create(chars);
    }
}
