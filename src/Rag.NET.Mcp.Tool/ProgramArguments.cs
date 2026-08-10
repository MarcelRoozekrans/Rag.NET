namespace Rag.NET.Mcp.Tool;

/// <summary>
/// Parses the command-line arguments <c>Program.cs</c> accepts: <c>--transport</c>,
/// <c>--port</c>, and <c>--api-key</c>.
/// </summary>
/// <remarks>
/// A named, <see langword="internal"/> type rather than a local function inside the top-level
/// <c>Program.cs</c>: a local function in a top-level program compiles onto that file's
/// compiler-generated <c>Program</c> class as <see langword="private"/>, which no
/// <c>InternalsVisibleTo</c> can reach — there is no way to unit-test it without either this
/// move or launching the executable itself. This type carries the one piece of
/// <c>Program.cs</c>'s behaviour that is pure enough to assert on directly: given the raw
/// argument array, what value (if any) does a flag resolve to. Everything else <c>Program.cs</c>
/// still does — choosing the stdio host over the web host, and actually running either one — is
/// launching a process or a network listener, not a computation, and is not exercised here.
/// </remarks>
internal static class ProgramArguments
{
    /// <summary>
    /// Finds the value supplied for <paramref name="name"/> in <paramref name="args"/>.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <param name="name">The flag to look for, including its leading dashes (e.g. <c>--port</c>).</param>
    /// <returns>
    /// The value from a <c>--name=value</c> token, or the following token for a bare
    /// <c>--name value</c> pair; <see langword="null"/> when <paramref name="name"/> is absent,
    /// or when it is the last token and so has no following value.
    /// </returns>
    /// <remarks>
    /// The flag name is matched case-insensitively; the value itself is returned verbatim.
    /// </remarks>
    public static string? Parse(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }

            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is present as a bare flag.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <param name="name">The flag, including its leading dashes (e.g. <c>--allow-anonymous</c>).</param>
    /// <returns>
    /// <see langword="true"/> when the flag appears as its own token. Unlike
    /// <see cref="Parse"/> this takes no value: a flag that switches off a safety check should
    /// not be satisfiable by <c>--allow-anonymous=false</c> reading as "present, therefore on".
    /// </returns>
    public static bool HasFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
