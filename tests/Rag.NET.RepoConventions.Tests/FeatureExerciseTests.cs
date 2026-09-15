using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that every feature section in <c>docs/reference/features.md</c> marked
/// <c>**Status:** ✅ Done</c> also says what <b>exercises</b> it — an
/// <c>**Exercised by:**</c> line naming a test or benchmark that runs the real thing — or is on
/// the Milestone 6 work list with the phase that will supply one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FeatureClaimTests"/> asks whether a Done section names code that exists.
/// Milestone 5 showed that is necessary and not sufficient: <c>Rag.NET.GraphRag</c> existed,
/// was Done, was green, and had eight defects that running it once found. So Milestone 6.0 asks
/// the next question of every Done row — <i>what runs it for real?</i> — and makes the answer a
/// line in the file, checked here, rather than a feeling.
/// </para>
/// <para>
/// <b>The line's grammar</b>, kept small so it can be checked: <c>**Exercised by:** kind — text</c>,
/// where <c>kind</c> is one of <c>benchmark</c> (a pinned figure with a control), <c>container</c>
/// (a Docker-tier suite against the real dependency), <c>test</c> (a fast-tier test that drives
/// the real path — a real file, a real pipeline), <c>recorded</c> (a scrubbed real-service
/// exchange replayed), or <c>declared</c> (cannot be exercised here, and the text says why). For
/// every kind but <c>declared</c>, the text must name at least one class in backticks that exists
/// as a <c>.cs</c> file under <c>tests/</c> or <c>benchmarks/</c> — the repository's analyzers
/// keep file name and type name equal, so that is an existence check, not a spelling one.
/// </para>
/// <para>
/// <b>Sections without the line are the work list</b>, pinned by heading in
/// <see cref="SectionsAwaitingExercise"/> under the Milestone 6 phase that owes them one, with the
/// same allowlist-and-staleness discipline as the ledgers beside this file: an unlisted Done
/// section without the line fails outright, a listed section that gains the line fails the
/// staleness test until its entry is deleted, and the Definition of Done requires the list to
/// reach empty.
/// </para>
/// </remarks>
public sealed partial class FeatureExerciseTests
{
    private const string FeaturesFileRelativePath = "docs/reference/features.md";
    private const string DoneStatusMarker = "**Status:** ✅ Done";
    private const string ExercisedMarker = "**Exercised by:**";

    /// <summary>The 60 Done sections today (a heading with several Done lines, like SaaS Connectors, is one). Far fewer means the parse lost the file's shape. Was 51 until 2026-09-03, when nine sections written as `**Status:** Delivered` were normalised to the marker this guard matches — they had been outside it entirely.</summary>
    private const int FewestPlausibleDoneSections = 45;

    private static readonly string[] Kinds = ["benchmark", "container", "test", "recorded", "declared"];

    /// <summary>
    /// Done sections that do not yet say what exercises them, each under the Milestone 6 phase
    /// that owes it a run — 6.1 live services (a recording, or <c>declared</c> with the reason),
    /// 6.2 real files / stores / runs, 6.2.1 retrieval techniques and answer engines (a pinned
    /// figure with a control). Written 2026-08-15 by Phase 6.0's first classification.
    /// </summary>
    private static readonly Dictionary<string, string> SectionsAwaitingExercise = new(StringComparer.Ordinal)
    {
        // Empty, and that is criterion 3 of Milestone 6's Definition of Done met: "every `✅ Done`
        // row in features.md names what exercises it". Twenty-nine sections were listed here until
        // 2026-09-15, each with the phase that owed it a real run.
        //
        // Nine were STALE rather than unstarted — the evidence existed and nothing pointed at it.
        // The web trio run against real sockets, audio transcribes through a real Whisper model,
        // OCR replays a cassette recorded against a real resource, and the connectors were declared
        // per package by #631.
        //
        // Twenty carry `declared` and say, in the published document, what does NOT exercise them.
        // That is the honest outcome rather than the flattering one, and the `declared` kind exists
        // for it. Changing their status instead would have removed them from this guard's scope,
        // which hides a gap rather than stating it.
        //
        // Do not re-add a section here to quiet a failure. Write the line — `declared` is available
        // and says more than an entry on a work list ever did.

    };

    [Fact]
    public void TheScanFindsTheDoneSections()
    {
        var sections = ParseDoneSections();
        Assert.True(
            sections.Count >= FewestPlausibleDoneSections,
            $"Parsed only {sections.Count} Done sections from {FeaturesFileRelativePath}, expected " +
            $"at least {FewestPlausibleDoneSections}. A guard that parses nothing passes for the " +
            "wrong reason, so this fails instead.");
    }

    [Fact]
    public void EveryDoneSectionSaysWhatExercisesIt_OrIsOwnedByAPhase()
    {
        var sections = ParseDoneSections();
        var awaiting = new List<string>();

        foreach (var section in sections)
        {
            if (section.ExercisedBy is null)
            {
                Assert.True(
                    SectionsAwaitingExercise.ContainsKey(section.Heading),
                    $"'{section.Heading}' is marked '{DoneStatusMarker}' ({FeaturesFileRelativePath} " +
                    $"line {section.HeadingLine}) and has no '{ExercisedMarker}' line, and " +
                    $"{nameof(SectionsAwaitingExercise)} has no entry for it. Either add the line — " +
                    $"'{ExercisedMarker} <kind> — text naming a `TestOrBenchmarkClass`', kind one of " +
                    $"{string.Join("/", Kinds)} — or list the section under the Milestone 6 phase that " +
                    "owes it a real run. A Done row nothing runs is how GraphRAG shipped with eight defects.");
                awaiting.Add(section.Heading);
                continue;
            }

            AssertWellFormed(section);
        }

        Assert.SkipWhen(
            awaiting.Count > 0,
            $"{awaiting.Count} of {sections.Count} Done sections do not yet say what exercises them, " +
            $"each owned by a Milestone 6 phase in {nameof(SectionsAwaitingExercise)}. The Definition " +
            "of Done requires zero before v1.0.");
    }

    [Fact]
    public void EverySectionAwaitingExerciseIsStillAwaiting()
    {
        // The staleness twin: an entry whose section gained its line, or whose heading no longer
        // exists as a Done section, is stale and fails until it is deleted.
        var byHeading = new Dictionary<string, DoneSection>(StringComparer.Ordinal);
        foreach (var section in ParseDoneSections())
        {
            byHeading[section.Heading] = section;
        }

        foreach (var (heading, owner) in SectionsAwaitingExercise)
        {
            Assert.True(
                byHeading.TryGetValue(heading, out var section),
                $"'{heading}' is listed in {nameof(SectionsAwaitingExercise)} ({owner}) but is not a " +
                $"Done section of {FeaturesFileRelativePath} any more — renamed, demoted, or removed. " +
                "Delete or rename its entry: the list must say only what is still true.");
            Assert.True(
                section!.ExercisedBy is null,
                $"'{heading}' is listed in {nameof(SectionsAwaitingExercise)} ({owner}) but now carries " +
                $"'{ExercisedMarker}'. Delete its entry: the work is done and the list must shrink to say so.");
        }
    }

    private static void AssertWellFormed(DoneSection section)
    {
        var text = section.ExercisedBy!;
        var kindMatch = ExercisedLine().Match(text);
        Assert.True(
            kindMatch.Success && Array.IndexOf(Kinds, kindMatch.Groups["kind"].Value) >= 0,
            $"'{section.Heading}' has '{ExercisedMarker}' ({FeaturesFileRelativePath} line " +
            $"{section.ExercisedLine}) but it does not read '<kind> — text' with kind one of " +
            $"{string.Join("/", Kinds)}: '{text}'.");

        var kind = kindMatch.Groups["kind"].Value;
        if (string.Equals(kind, "declared", StringComparison.Ordinal))
        {
            Assert.True(
                kindMatch.Groups["text"].Value.Trim().Length >= 20,
                $"'{section.Heading}' is 'declared' but says almost nothing about why it cannot be " +
                "exercised here. A declaration names what would be needed and what stays unverified.");
            return;
        }

        var repositoryRoot = TestProject.FindRepositoryRoot();
        var resolved = false;
        foreach (Match name in BacktickedName().Matches(kindMatch.Groups["text"].Value))
        {
            if (TestOrBenchmarkClassExists(repositoryRoot, name.Groups["name"].Value))
            {
                resolved = true;
                break;
            }
        }

        Assert.True(
            resolved,
            $"'{section.Heading}' says '{ExercisedMarker} {text}' ({FeaturesFileRelativePath} line " +
            $"{section.ExercisedLine}) but no backticked name in it is a `.cs` file under tests/ or " +
            "benchmarks/. The pointer must name the class that runs the real thing, so a rename or " +
            "a deletion breaks this line rather than leaving a claim behind.");
    }

    private static bool TestOrBenchmarkClassExists(string repositoryRoot, string className)
    {
        foreach (var top in new[] { "tests", "benchmarks" })
        {
            var root = Path.Combine(repositoryRoot, top);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, className + ".cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<DoneSection> ParseDoneSections()
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), "docs", "reference", "features.md");
        var lines = File.ReadAllLines(path);
        var sections = new List<DoneSection>();
        var heading = "(before the first heading)";
        var headingLine = 0;
        var start = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal) || lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                AddIfDone(sections, lines, start, i, heading, headingLine);
                heading = lines[i].TrimStart('#').Trim();
                headingLine = i + 1;
                start = i + 1;
            }
        }

        AddIfDone(sections, lines, start, lines.Length, heading, headingLine);
        return sections;
    }

    private static void AddIfDone(List<DoneSection> sections, string[] lines, int start, int end, string heading, int headingLine)
    {
        var done = false;
        string? exercised = null;
        var exercisedLine = 0;
        for (var i = start; i < end; i++)
        {
            if (lines[i].Contains(DoneStatusMarker, StringComparison.Ordinal))
            {
                done = true;
            }

            if (lines[i].StartsWith(ExercisedMarker, StringComparison.Ordinal))
            {
                exercised = lines[i][ExercisedMarker.Length..].Trim();
                exercisedLine = i + 1;
            }
        }

        if (done)
        {
            sections.Add(new DoneSection(heading, headingLine, exercised, exercisedLine));
        }
    }

    [GeneratedRegex(@"^(?<kind>[a-z]+)\s+—\s+(?<text>.+)$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ExercisedLine();

    [GeneratedRegex(@"`(?<name>[A-Za-z0-9_]+)`", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex BacktickedName();

    private sealed record DoneSection(string Heading, int HeadingLine, string? ExercisedBy, int ExercisedLine);
}
