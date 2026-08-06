using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Guards against XML doc <c>&lt;summary&gt;</c> text in <c>src/Rag.NET.Abstractions</c> that
/// satisfies CS1591 while telling a reader nothing: a summary that, once normalised, is just the
/// member's own name split back into words.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> Normalise the summary — lower-case, strip XML tags and punctuation, drop a
/// leading "gets or sets" / "gets" / "sets", then drop a leading "the" / "a" / "an" — and
/// normalise the member name by splitting it on camelCase boundaries and lower-casing it. If the
/// two strings are then equal, the summary restates the name and nothing else, so the assertion
/// fails. For example <c>TopK</c> documented as "Gets the top k." normalises both sides to
/// "top k" and fails; "Chunks to retrieve for this query." does not.
/// </para>
/// <para>
/// <b>What this does not catch.</b> This is a name-restatement detector, not a comprehension
/// check. A fluent sentence that says almost nothing — "Retrieves configured settings." on a
/// member named <c>GetSettings</c> — passes, because it does not literally equal the normalised
/// name. Passing this guard is a floor, not evidence that a summary is good: it only clears the
/// laziest form of filler, the kind that is the member name with spaces and a capital letter.
/// </para>
/// <para>
/// <b>Presence is not this file's job.</b> A member with no doc comment at all, or a doc comment
/// with no <c>&lt;summary&gt;</c> tag, is skipped here rather than failed. Once
/// <c>GenerateDocumentationFile</c> is enabled for <c>src/</c>, CS1591 — a build error under this
/// repository's <c>TreatWarningsAsErrors</c> — already demands presence for every externally
/// visible member. Duplicating that here would fail this suite for every one of the 172 members
/// Abstractions had undocumented at the start of the XML-documentation phase, for an absence this
/// file was never meant to catch. It only ever judges a summary somebody already wrote.
/// </para>
/// <para>
/// <b>Scope: <c>src/Rag.NET.Abstractions</c> only.</b> That package holds all 172 of this phase's
/// undocumented members; the phase's own measurement found the other 65 shipped packages already
/// at zero. Scanning them too would add scan surface with nothing at stake yet, and any
/// pre-existing summary there that happened to trip this rule would block this phase's Tasks 1–2
/// on a defect outside their scope. Widening the scope repository-wide, once every package
/// carries the same documentation discipline Abstractions is about to, is future work — not
/// something this guard does today.
/// </para>
/// </remarks>
public sealed partial class DocumentationQualityTests
{
    private const string AbstractionsRelativeDirectory = "src/Rag.NET.Abstractions";

    /// <summary>
    /// Abstractions carries roughly 330 <c>&lt;summary&gt;</c> blocks today. Far fewer means the
    /// scan lost the tree, or stopped associating summaries with the declarations that follow
    /// them, and would pass over nothing — silently, forever.
    /// </summary>
    private const int FewestPlausibleDocumentedMembers = 250;

    private static readonly char[] DeclarationTerminators = ['(', '{', ';', ','];

    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.Ordinal) { "the", "a", "an" };

    [Fact]
    public void TheScanFindsPlausiblyManyDocumentedMembers()
    {
        var members = ParseDocumentedMembers();

        Assert.True(
            members.Count >= FewestPlausibleDocumentedMembers,
            $"Found only {members.Count} documented members under " +
            $"{AbstractionsRelativeDirectory}, expected at least " +
            $"{FewestPlausibleDocumentedMembers}. A guard that stops associating summaries with " +
            "declarations passes for the wrong reason, so this fails instead.");
    }

    [Fact]
    public void NoDocumentedMemberInAbstractionsHasASummaryThatOnlyRestatesItsName()
    {
        var violations = new List<string>();

        foreach (var member in ParseDocumentedMembers())
        {
            if (IsVacuous(member.PlainSummary, member.MemberName))
            {
                violations.Add(
                    $"{member.RelativePath}:{member.LineNumber}: '{member.MemberName}' — " +
                    $"\"{member.PlainSummary}\"");
            }
        }

        Assert.True(
            violations.Count == 0,
            "The following summaries, once normalised, are exactly their member's own name " +
            "split back into words — filler that satisfies CS1591 and tells an IntelliSense " +
            "reader nothing the signature did not already say. Say what the member does, means, " +
            "or is for instead:" + Environment.NewLine + "  - " +
            string.Join(Environment.NewLine + "  - ", violations));
    }

    /// <summary>
    /// Pins the vacuousness rule to the worked examples it was specified against, independent of
    /// what currently lives in Abstractions.
    /// </summary>
    /// <param name="memberName">The member name half of the check.</param>
    /// <param name="summary">The (already tag-stripped) summary text half of the check.</param>
    /// <param name="expectedVacuous">Whether the pair is expected to be flagged.</param>
    [Theory]
    [InlineData("TopK", "Gets the top k.", true)]
    [InlineData("TopK", "Chunks to retrieve for this query.", false)]
    [InlineData("SkipCompression", "Bypass contextual compression for this call.", false)]
    public void TheVacuousnessRuleMatchesItsWorkedExamples(string memberName, string summary, bool expectedVacuous)
    {
        Assert.Equal(expectedVacuous, IsVacuous(summary, memberName));
    }

    private static bool IsVacuous(string plainSummary, string memberName)
    {
        var normalisedSummary = NormaliseSummary(plainSummary);
        return normalisedSummary.Length > 0 &&
            string.Equals(normalisedSummary, NormaliseMemberName(memberName), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lower-cases, strips anything that is not a letter/digit/space, then drops a leading
    /// "gets or sets" / "gets" / "sets" and, after that, a leading "the" / "a" / "an".
    /// </summary>
    /// <param name="text">Already tag-stripped summary text.</param>
    /// <returns>The normalised words, space-joined.</returns>
    private static string NormaliseSummary(string text)
    {
        var lowered = text.ToLowerInvariant();
        var letters = NonWordCharacter().Replace(lowered, " ");
        var words = new List<string>(letters.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (words.Count >= 3 &&
            string.Equals(words[0], "gets", StringComparison.Ordinal) &&
            string.Equals(words[1], "or", StringComparison.Ordinal) &&
            string.Equals(words[2], "sets", StringComparison.Ordinal))
        {
            words.RemoveRange(0, 3);
        }
        else if (words.Count >= 1 &&
            (string.Equals(words[0], "gets", StringComparison.Ordinal) ||
             string.Equals(words[0], "sets", StringComparison.Ordinal)))
        {
            words.RemoveAt(0);
        }

        if (words.Count > 0 && LeadingArticles.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>Splits a member name on camelCase boundaries and lower-cases it.</summary>
    /// <param name="name">The member's identifier, as declared in source.</param>
    /// <returns>The identifier's words, space-joined and lower-cased.</returns>
    private static string NormaliseMemberName(string name) =>
        CamelCaseBoundary().Replace(name, " ").ToLowerInvariant();

    // Lookaround is unsupported by RegexOptions.NonBacktracking (see
    // ReservedMetadataKeyGuardTests.ReservedKeyAssignment for the same constraint), so this
    // pattern omits it.
    [GeneratedRegex(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CamelCaseBoundary();

    [GeneratedRegex(@"[^a-z0-9\s]", RegexOptions.NonBacktracking)]
    private static partial Regex NonWordCharacter();

    private sealed record DocumentedMember(string RelativePath, int LineNumber, string MemberName, string PlainSummary);

    private static IReadOnlyList<DocumentedMember> ParseDocumentedMembers()
    {
        var repositoryRoot = TestProject.FindRepositoryRoot();
        var directory = Path.Combine(repositoryRoot, AbstractionsRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var members = new List<DocumentedMember>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, directory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            members.AddRange(ParseFile(File.ReadAllLines(file), relativePath));
        }

        return members;
    }

    /// <summary>
    /// Walks one file top to bottom, pairing each <c>///</c> doc-comment block with the
    /// declaration line that follows it (skipping blank lines and attributes in between).
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="relativePath">The file's repository-relative path, for failure messages.</param>
    /// <returns>One entry per doc block whose summary and declaration were both recognised.</returns>
    private static List<DocumentedMember> ParseFile(string[] lines, string relativePath)
    {
        var members = new List<DocumentedMember>();
        var index = 0;

        while (index < lines.Length)
        {
            if (!IsDocCommentLine(lines[index]))
            {
                index++;
                continue;
            }

            var docStart = index;
            while (index < lines.Length && IsDocCommentLine(lines[index]))
            {
                index++;
            }

            var summary = ExtractSummary(lines, docStart, index);
            var declarationStart = SkipToDeclaration(lines, index);

            if (summary is not null && declarationStart < lines.Length)
            {
                var name = ExtractMemberName(BuildDeclarationText(lines, declarationStart));
                if (name is not null)
                {
                    members.Add(new DocumentedMember(relativePath, declarationStart + 1, name, summary));
                }
            }

            index = declarationStart < lines.Length ? declarationStart + 1 : declarationStart;
        }

        return members;
    }

    private static bool IsDocCommentLine(string line) =>
        line.TrimStart().StartsWith("///", StringComparison.Ordinal);

    /// <summary>Extracts and tag-strips the first <c>&lt;summary&gt;</c> in a doc-comment block.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="start">The doc block's first line, inclusive.</param>
    /// <param name="end">The doc block's last line, exclusive.</param>
    /// <returns>The plain summary text, or <see langword="null"/> if the block has none.</returns>
    private static string? ExtractSummary(string[] lines, int start, int end)
    {
        var joined = string.Join(' ', lines[start..end].Select(StripDocSlashes));
        var match = SummaryTag().Match(joined);
        if (!match.Success)
        {
            return null;
        }

        var plain = StripSelfClosingTags().Replace(match.Groups["body"].Value, " ");
        plain = StripRemainingTags().Replace(plain, " ");
        plain = CollapseWhitespace().Replace(plain, " ").Trim();
        return plain.Length == 0 ? null : plain;
    }

    private static string StripDocSlashes(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed[3..].Trim() : trimmed;
    }

    [GeneratedRegex(@"<summary>(?<body>.*?)</summary>", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SummaryTag();

    [GeneratedRegex(@"<[^>]*/>", RegexOptions.NonBacktracking)]
    private static partial Regex StripSelfClosingTags();

    [GeneratedRegex(@"</?[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex StripRemainingTags();

    [GeneratedRegex(@"\s+", RegexOptions.NonBacktracking)]
    private static partial Regex CollapseWhitespace();

    /// <summary>Advances past blank lines and attributes to the declaration a doc block documents.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="index">The first line after the doc-comment block.</param>
    /// <returns>The index of the declaration line, or <paramref name="lines"/>'s length if none remains.</returns>
    private static int SkipToDeclaration(string[] lines, int index)
    {
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                index = SkipAttribute(lines, index);
                continue;
            }

            return index;
        }

        return index;
    }

    private static int SkipAttribute(string[] lines, int index)
    {
        var balance = 0;
        while (index < lines.Length)
        {
            foreach (var character in lines[index])
            {
                if (character == '[')
                {
                    balance++;
                }
                else if (character == ']')
                {
                    balance--;
                }
            }

            index++;
            if (balance <= 0)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// Joins the declaration's first physical line, and any continuation lines up to a bounded
    /// look-ahead, into one string — stopping as soon as a line carries a shape-defining token
    /// (<c>(</c>, <c>{</c>, <c>;</c>, <c>,</c>, or <c>=&gt;</c>). Nearly every declaration in this
    /// project is one physical line, so the look-ahead exists only for the few that wrap.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="declarationStart">The declaration's first line.</param>
    /// <returns>The declaration text, space-joined.</returns>
    private static string BuildDeclarationText(string[] lines, int declarationStart)
    {
        var parts = new List<string>();
        var index = declarationStart;
        var limit = Math.Min(lines.Length, declarationStart + 8);

        while (index < limit)
        {
            var trimmed = lines[index].Trim();
            index++;
            if (trimmed.Length == 0)
            {
                continue;
            }

            parts.Add(trimmed);
            if (trimmed.IndexOfAny(DeclarationTerminators) >= 0 || trimmed.Contains("=>", StringComparison.Ordinal))
            {
                break;
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Recognises the member a declaration text names: a type (class/interface/struct/enum/
    /// record), an enum member, a method, a brace-bodied property/indexer, an expression-bodied
    /// member, or a field/const. Returns <see langword="null"/> for shapes it does not recognise
    /// (for example operator overloads) rather than guess — an unassociated summary is simply not
    /// checked, which is the safer failure mode for a heuristic source-text parser.
    /// </summary>
    /// <param name="text">The declaration text built by <see cref="BuildDeclarationText"/>.</param>
    /// <returns>The member's name, or <see langword="null"/>.</returns>
    private static string? ExtractMemberName(string text) =>
        TypeDeclarationName(text)
        ?? EnumMemberName(text)
        ?? MethodName(text)
        ?? PropertyName(text)
        ?? ArrowMemberName(text)
        ?? FieldName(text);

    private static string? TypeDeclarationName(string text)
    {
        var match = TypeDeclarationPattern().Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(
        @"\b(?:class|interface|struct|enum|delegate|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex TypeDeclarationPattern();

    private static string? EnumMemberName(string text)
    {
        if (text.IndexOfAny(['(', '{', ';']) >= 0 || text.Contains("=>", StringComparison.Ordinal))
        {
            return null;
        }

        var match = EnumMemberPattern().Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*)", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex EnumMemberPattern();

    private static string? MethodName(string text)
    {
        // Operators are excluded rather than mis-parsed: "operator string(...)" would otherwise
        // read as a method literally named "string".
        if (!text.Contains('(') || text.Contains(" operator ", StringComparison.Ordinal))
        {
            return null;
        }

        var match = MethodNamePattern().Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex MethodNamePattern();

    private static string? PropertyName(string text)
    {
        if (!text.Contains('{'))
        {
            return null;
        }

        var match = PropertyNamePattern().Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*\{", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex PropertyNamePattern();

    private static string? ArrowMemberName(string text)
    {
        if (!text.Contains("=>", StringComparison.Ordinal))
        {
            return null;
        }

        var match = ArrowMemberPattern().Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*=>", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ArrowMemberPattern();

    private static string? FieldName(string text)
    {
        var assignment = FieldAssignmentPattern().Match(text);
        if (assignment.Success)
        {
            return assignment.Groups["name"].Value;
        }

        var semicolon = FieldSemicolonPattern().Match(text);
        return semicolon.Success ? semicolon.Groups["name"].Value : null;
    }

    // The trailing [^=] excludes "==" (a comparison, not a field initialiser).
    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*=[^=]", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FieldAssignmentPattern();

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*;", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FieldSemicolonPattern();

    private static bool IsBuildOutput(string file, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, file);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

        return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
    }
}
