using System.Globalization;

namespace Rag.NET.Cli;

/// <summary>
/// Parses <c>ragnet</c>'s command line: the command name, the positional argument (a path or a
/// question), and the <c>--top-k</c>/<c>--overwrite</c> options.
/// </summary>
/// <remarks>
/// A named, <see langword="internal"/> type rather than logic inline in the top-level
/// <c>Program.cs</c> — the same move <c>Rag.NET.Mcp.Tool</c>'s <c>ProgramArguments</c> made, and
/// for the same reason: a local function in a top-level program compiles onto that file's
/// compiler-generated <c>Program</c> class as <see langword="private"/>, unreachable from a test
/// assembly no matter what <c>InternalsVisibleTo</c> declares. Everything this type does is pure —
/// given the raw argument array, what command and options does it resolve to — which is exactly
/// what makes it assertable without launching this project's executable.
/// </remarks>
internal static class CliArguments
{
    private const int DefaultTopK = 5;

    /// <summary>Parses <paramref name="args"/> into a <see cref="ParsedArguments"/>.</summary>
    /// <remarks>
    /// No command line argument is validated against what the resolved command actually needs
    /// (for example, an empty <see cref="ParsedArguments.Argument"/>) — that is the caller's job,
    /// once it knows which command was requested.
    /// </remarks>
    public static ParsedArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || IsHelpFlag(args[0]))
        {
            return new ParsedArguments(null, null, DefaultTopK, false, ShowHelp: true);
        }

        var command = args[0];
        var positional = new List<string>();
        var topK = DefaultTopK;
        var overwrite = false;
        var showHelp = false;

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];

            if (IsHelpFlag(token))
            {
                showHelp = true;
            }
            else if (string.Equals(token, "--overwrite", StringComparison.OrdinalIgnoreCase))
            {
                overwrite = true;
            }
            else if (string.Equals(token, "--top-k", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTopK))
            {
                topK = parsedTopK;
                i++;
            }
            else
            {
                positional.Add(token);
            }
        }

        var argument = positional.Count > 0 ? string.Join(' ', positional) : null;
        return new ParsedArguments(command, argument, topK, overwrite, showHelp);
    }

    private static bool IsHelpFlag(string token) =>
        string.Equals(token, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "-h", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The command line, parsed. <paramref name="Command"/> is <see langword="null"/> when no
    /// arguments were supplied, or when <c>--help</c>/<c>-h</c> came first — both cases set
    /// <paramref name="ShowHelp"/> instead. <paramref name="Argument"/> is every positional token
    /// after the command, space-joined: a file or directory path for <c>ingest</c>, a (possibly
    /// multi-word, unquoted) question for <c>query</c>. <see langword="null"/> when the command
    /// had no positional tokens at all.
    /// </summary>
    public sealed record ParsedArguments(
        string? Command, string? Argument, int TopK, bool Overwrite, bool ShowHelp);
}
