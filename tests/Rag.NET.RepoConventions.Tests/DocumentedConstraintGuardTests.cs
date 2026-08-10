using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that when an options property's XML doc comment states a numeric constraint —
/// "must be greater than 0", "Range: 0.0–1.0", "[0, 1]" — something in production actually
/// enforces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Origin.</b> The issue #90 audit of ~112 options classes found doc comments promising
/// constraints that nothing checked: <c>RetrievalOptions.CragScoreThreshold</c> said
/// "Range: 0.0–1.0" in the same record where <c>RedundancyThreshold</c> and <c>MmrLambda</c>
/// are checked by <c>PipelineRetriever.RetrieveAsync</c>; <c>EnsembleOptions.DenseWeight</c>
/// and <c>Bm25Weight</c> said "Must be in the range [0, 1]" three lines from a property that
/// honestly documents that weights are not validated; <c>MultiQueryOptions.VariantCount</c>
/// said "Must be at least 1" while the identical claim on <c>HydeOptions.HypothesisCount</c>
/// is enforced in the same registration file. A documented constraint nothing enforces is a
/// promise the configuration surface silently breaks — the class of defect behind the
/// security fixes in #92, the ingestion hang in #93, and the silent-empty-result settings in
/// #94.
/// </para>
/// <para>
/// <b>What counts as a claim.</b> Doc-comment lines attached directly to a property
/// declaration, in files named <c>*Options.cs</c> under <c>src/</c>, matching one of the
/// phrase shapes in <see cref="ConstraintClaim"/>. <b>What counts as enforcement</b> — any
/// one of five shapes, each pinned to a live example by
/// <see cref="TheScanSeesEveryShapeEnforcementTakes"/>: a ZeroAlloc validation attribute on
/// the property in a <c>[Validate]</c> class (the generated validator's production invocation
/// is <see cref="InertValidatorGuardTests"/>' subject); a member-access comparison of the
/// property against something (<c>options.VariantCount &lt; 1</c>); a
/// <c>ThrowIf*</c> helper naming the property; <c>Math.Clamp</c> over the property; or
/// <c>nameof(Property)</c> in the declaring file, the shape of a self-validating setter's
/// throw message. Comment lines are stripped before evidence is searched, so prose cannot
/// satisfy an assertion about what runs.
/// </para>
/// <para>
/// <b>What this deliberately does NOT catch — do not mistake it for coverage.</b> Nothing
/// mechanical finds a semantically incorrect bound: the bug that started this work,
/// <c>ChunkingOptions.Overlap</c>, carried <c>[GreaterThan(0)]</c> beside a doc comment saying
/// "must be greater than 0" — code and docs agreed, and both were wrong. This guard would have
/// called that enforced. It also does not see: constraints stated on type-level docs,
/// positional record parameters, non-<c>*Options.cs</c> types, or phrasings outside the listed
/// shapes; non-numeric constraints ("must be set", "must be non-blank"); and its enforcement
/// evidence is name-based text matching, so a behavioral comparison
/// (<c>if (options.Prop &gt; 0)</c> as a feature toggle) earns credit though it rejects
/// nothing, and same-named properties on different types share credit. The repository has a
/// documented history of guards that looked like coverage; this one is a tripwire for the
/// claim/enforcement gap, nothing more.
/// </para>
/// </remarks>
public sealed partial class DocumentedConstraintGuardTests
{
    /// <summary>
    /// There are 112 <c>*Options.cs</c> files under <c>src/</c> today — the issue #90 audit's
    /// ~112 options classes. Far fewer means the scan lost the working tree and is asserting
    /// over nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleOptionsFiles = 80;

    /// <summary>
    /// The scan finds 24 constraint-stating properties today. Same defence: a claim scan that
    /// finds almost nothing is vacuously green.
    /// </summary>
    private const int FewestPlausibleConstrainedProperties = 15;

    /// <summary>Longest a single evidence regex may run before the scan is declared broken.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Properties whose documented constraint the scan cannot see enforced, each with the
    /// reason — either enforcement exists in a shape the scan cannot attribute to the property,
    /// or leaving it unenforced is a recorded design decision with an owner. A constrained
    /// property with no detectable enforcement and no entry here fails
    /// <see cref="EveryDocumentedNumericConstraintIsEnforced"/>; an entry whose property gains
    /// detectable enforcement, or stops stating a constraint, fails
    /// <see cref="EveryLedgeredConstraintIsStillClaimedAndStillUnenforced"/> until it is
    /// deleted. Keys are <c>TypeName.PropertyName</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ConstraintsAllowedToRemainUnenforced =
        new(StringComparer.Ordinal)
        {
            // Kept nearly empty on purpose. When this guard first ran, every red property
            // turned out to be either a genuine defect (the issue #90 four —
            // CragScoreThreshold, DenseWeight, Bm25Weight, VariantCount — plus
            // SceneChangeThreshold, all fixed on the branch that added this file) or a
            // detection gap worth closing instead (FallbackChainOptions.PerClientTimeout was
            // enforced via a nullable-pattern extraction the scan could not yet see — the scan
            // was widened rather than the finding ledgered). Prefer that order: fix, then
            // widen, then ledger.
            ["RagOptions.RedundancyThreshold"] =
                "Enforced, but across a type boundary the scan cannot attribute: " +
                "RagPipeline.BuildRetrievalOptions copies it verbatim into " +
                "RetrievalOptions.RedundancyThreshold, whose [InclusiveBetween] attribute the " +
                "generated RetrievalOptionsValidator applies on every retrieval " +
                "(PipelineRetriever.RetrieveAsync). Until issue #100 that validator was a " +
                "manual comparison block in PipelineRetriever, and this property rode on " +
                "shared-name credit for it — the enforcement moved, the credit could not " +
                "follow. Every consuming path (RagPipeline directly, or via " +
                "PromptHardeningAnswerEngineDecorator's RagOptions copy) reaches retrieval " +
                "through BuildRetrievalOptions, so no unvalidated path remains.",
        };

    [Fact]
    public void TheScanFindsTheConstraintSurface()
    {
        var optionsFiles = CountOptionsFiles();
        var constrained = FindConstrainedProperties();

        Assert.True(
            optionsFiles >= FewestPlausibleOptionsFiles,
            $"Found only {optionsFiles} *Options.cs files under src/, expected at least " +
            $"{FewestPlausibleOptionsFiles}. A constraint guard that scans nothing passes for " +
            "the wrong reason, so this fails instead.");

        Assert.True(
            constrained.Count >= FewestPlausibleConstrainedProperties,
            $"Found only {constrained.Count} properties whose doc comments state a numeric " +
            $"constraint, expected at least {FewestPlausibleConstrainedProperties}. Either the " +
            "doc comments moved or the claim patterns regressed.");
    }

    /// <summary>
    /// Pins every phrase shape a documented constraint takes, one live example per shape.
    /// Losing a shape narrows the scan silently — the enforcement test would still pass, over
    /// fewer claims — so each shape is held by name here instead.
    /// </summary>
    /// <param name="typeName">The declaring options type.</param>
    /// <param name="propertyName">The property whose doc comment uses the shape.</param>
    /// <param name="shape">How the claim is phrased, for the failure message.</param>
    [Theory]
    [InlineData("ChunkingOptions", "MaxChunkSize", "'Must be greater than 0'")]
    [InlineData("MultiQueryOptions", "VariantCount", "'Must be at least 1'")]
    [InlineData("RetrievalOptions", "CragScoreThreshold", "'Range: 0.0-1.0' (range keyword)")]
    [InlineData("EnsembleOptions", "DenseWeight", "'Must be in the range [0, 1]'")]
    [InlineData("VideoDescriptionOptions", "SceneChangeThreshold", "parenthesised '(0.0-1.0)'")]
    [InlineData("OnnxTokenEmbeddingOptions", "WindowOverlapTokens", "'Must be non-negative'")]
    public void TheScanSeesEveryShapeAConstraintClaimTakes(string typeName, string propertyName, string shape)
    {
        Assert.True(
            FindProperty(typeName, propertyName) is not null,
            $"The scan no longer finds a constraint on {typeName}.{propertyName}, whose doc " +
            $"comment states one as {shape}. Either that phrase shape has stopped being " +
            "scanned — widen the claim patterns back rather than updating this example — or " +
            "the doc comment changed, in which case point this pin at another live use of the " +
            "same shape.");
    }

    /// <summary>
    /// Pins every evidence shape enforcement takes, one live example per shape. A lost evidence
    /// shape fails the main test loudly (false red), but only until someone "fixes" it by
    /// ledgering the fallout — so each shape is held by name here too.
    /// </summary>
    /// <param name="typeName">The declaring options type.</param>
    /// <param name="propertyName">The property enforced in the given shape.</param>
    /// <param name="shape">How the enforcement is written, for the failure message.</param>
    [Theory]
    [InlineData("ChunkingOptions", "MaxChunkSize", "a ZeroAlloc [GreaterThan] attribute in a [Validate] class")]
    [InlineData("TimeWeightedOptions", "DecayRate", "a ZeroAlloc [GreaterThanOrEqualTo] attribute in a [Validate] class")]
    [InlineData("RetrievalOptions", "RedundancyThreshold", "a ZeroAlloc [InclusiveBetween] attribute in a [Validate] record")]
    [InlineData("MultiQueryOptions", "VariantCount", "a member-access comparison in UseMultiQueryRetrieval (RagBuilderExtensions)")]
    [InlineData("CostBudgetOptions", "InputPricePerMTokens", "a ThrowIf* helper naming the property")]
    [InlineData("ShadowPipelineOptions", "SampleRate", "a self-validating setter whose throw message uses nameof")]
    [InlineData("FallbackChainOptions", "PerClientTimeout", "a nullable pattern extraction ('is { } t && t <= …') compared in the same expression")]
    public void TheScanSeesEveryShapeEnforcementTakes(string typeName, string propertyName, string shape)
    {
        var property = FindProperty(typeName, propertyName);

        Assert.True(
            property is not null,
            $"The scan no longer finds a constraint claim on {typeName}.{propertyName}, which " +
            $"this test uses as the live example of enforcement via {shape}. Point the pin at " +
            "another property enforced the same way.");

        Assert.True(
            IsEnforced(property!, LoadProductionSources()),
            $"The scan no longer sees {typeName}.{propertyName} as enforced, though it is — " +
            $"via {shape}. That evidence shape has stopped being detected, so every property " +
            "enforced that way is about to fail the main test falsely — widen the evidence " +
            "detection back rather than ledgering the fallout.");
    }

    [Fact]
    public void EveryDocumentedNumericConstraintIsEnforced()
    {
        var sources = LoadProductionSources();
        var failures = new List<string>();

        foreach (var property in FindConstrainedProperties())
        {
            if (ConstraintsAllowedToRemainUnenforced.ContainsKey(property.Key) ||
                IsEnforced(property, sources))
            {
                continue;
            }

            failures.Add(
                $"{property.RelativePath}:{property.Line}: {property.Key} states " +
                $"\"{property.Claim}\" and nothing detectably enforces it.");
        }

        Assert.True(
            failures.Count == 0,
            "The following options properties document a numeric constraint that nothing " +
            "enforces. A documented constraint nothing checks is a promise the configuration " +
            "surface silently breaks (issue #90). Enforce it — a ZeroAlloc validation " +
            "attribute in a [Validate] class, a check in the consumer or registration path " +
            "(PipelineRetriever.RetrieveAsync and UseHyde are the house shapes), or a " +
            "self-validating setter — or, if enforcement exists in a shape the scan cannot " +
            $"attribute or non-enforcement is a real design decision, record it in " +
            $"{nameof(ConstraintsAllowedToRemainUnenforced)} with the reason:" +
            Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", failures));
    }

    [Fact]
    public void EveryLedgeredConstraintIsStillClaimedAndStillUnenforced()
    {
        // The staleness guard, same discipline as the sibling ledgers: an allow-list nothing
        // re-checks is how a known gap becomes furniture. The moment a listed property gains
        // detectable enforcement — or stops stating the constraint at all — its entry is a hole
        // in the guard, so it fails here until it is deleted.
        var properties = FindConstrainedProperties();
        var sources = LoadProductionSources();

        foreach (var (key, reason) in ConstraintsAllowedToRemainUnenforced)
        {
            var property = FindByKey(properties, key);

            Assert.True(
                property is not null,
                $"No property doc comment states a constraint for '{key}' any more, so its " +
                $"entry in {nameof(ConstraintsAllowedToRemainUnenforced)} is stale. Delete the " +
                $"entry. It was recorded because: {reason}");

            Assert.False(
                IsEnforced(property!, sources),
                $"'{key}' now has detectable enforcement, so its entry in " +
                $"{nameof(ConstraintsAllowedToRemainUnenforced)} is stale. Delete the entry so " +
                $"{nameof(EveryDocumentedNumericConstraintIsEnforced)} guards the property " +
                $"against regressing. It was recorded because: {reason}");
        }
    }

    private static ConstrainedProperty? FindProperty(string typeName, string propertyName)
    {
        var key = $"{typeName}.{propertyName}";
        return FindByKey(FindConstrainedProperties(), key);
    }

    private static ConstrainedProperty? FindByKey(IReadOnlyList<ConstrainedProperty> properties, string key)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, key, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    private static int CountOptionsFiles()
    {
        var count = 0;
        foreach (var (relativePath, _) in TestProject.EnumerateSourceFiles("src"))
        {
            if (relativePath.EndsWith("Options.cs", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Scans every <c>*Options.cs</c> file under <c>src/</c> for properties whose attached doc
    /// comment states a numeric constraint.
    /// </summary>
    /// <returns>The constrained properties, in directory order.</returns>
    private static IReadOnlyList<ConstrainedProperty> FindConstrainedProperties()
    {
        var results = new List<ConstrainedProperty>();

        foreach (var (relativePath, fullPath) in TestProject.EnumerateSourceFiles("src"))
        {
            if (relativePath.EndsWith("Options.cs", StringComparison.Ordinal))
            {
                CollectFromFile(relativePath, File.ReadAllLines(fullPath), results);
            }
        }

        return results;
    }

    /// <summary>
    /// Walks one file line by line, attaching each run of <c>///</c> lines (and any attribute
    /// lines between them and the member) to the property declaration that follows. Any other
    /// line breaks the attachment, so a type-level doc comment never leaks onto the first
    /// property.
    /// </summary>
    /// <param name="relativePath">The file, relative to the repository root.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="results">Receives each property whose doc states a constraint.</param>
    private static void CollectFromFile(string relativePath, string[] lines, List<ConstrainedProperty> results)
    {
        var typeName = "(unknown type)";
        var doc = new StringBuilder();
        var attributes = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                _ = doc.Append(trimmed).Append(' ');
                continue;
            }

            // Plain comments neither carry claims nor break attachment — and they must never
            // reach the type matcher: RetrievalOptions.cs once carried a comment ending in
            // "record types.", which would otherwise have renamed the type to "types".
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Before the property matcher, which would otherwise read "public sealed class X"
            // as a property named X and swallow the type name.
            var type = TypeDeclaration().Match(lines[i]);
            if (type.Success)
            {
                typeName = type.Groups["name"].Value;
                doc.Clear();
                attributes.Clear();
                continue;
            }

            var property = PropertyDeclaration().Match(lines[i]);
            if (property.Success)
            {
                RecordIfConstrained(relativePath, i + 1, typeName, property.Groups["name"].Value,
                    attributes.ToString() + lines[i], doc.ToString(), results);
                doc.Clear();
                attributes.Clear();
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                _ = attributes.Append(trimmed).Append(' ');
                continue;
            }

            if (trimmed.Length > 0)
            {
                doc.Clear();
                attributes.Clear();
            }
        }
    }

    private static void RecordIfConstrained(
        string relativePath, int line, string typeName, string propertyName,
        string declaration, string doc, List<ConstrainedProperty> results)
    {
        var claim = ConstraintClaim().Match(doc);
        if (claim.Success)
        {
            results.Add(new ConstrainedProperty(
                relativePath, line, $"{typeName}.{propertyName}", propertyName, claim.Value, declaration));
        }
    }

    /// <summary>
    /// Reads every C# file under <c>src/</c> with comment lines removed — evidence must live in
    /// code that runs, never in prose about it.
    /// </summary>
    /// <returns>Each file's stripped text with its repository-relative path.</returns>
    private static IReadOnlyList<(string RelativePath, string Text)> LoadProductionSources()
    {
        var sources = new List<(string, string)>();

        foreach (var (relativePath, fullPath) in TestProject.EnumerateSourceFiles("src"))
        {
            var builder = new StringBuilder();
            foreach (var line in File.ReadLines(fullPath))
            {
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    _ = builder.Append(line).Append('\n');
                }
            }

            sources.Add((relativePath, builder.ToString()));
        }

        return sources;
    }

    /// <summary>
    /// Decides whether anything detectably enforces the property's documented constraint. The
    /// five accepted shapes are pinned by <see cref="TheScanSeesEveryShapeEnforcementTakes"/>;
    /// see the type remarks for what none of them can catch.
    /// </summary>
    /// <param name="property">The constrained property.</param>
    /// <param name="sources">Every production source file, comments stripped.</param>
    /// <returns>Whether enforcement evidence exists.</returns>
    private static bool IsEnforced(
        ConstrainedProperty property, IReadOnlyList<(string RelativePath, string Text)> sources)
    {
        if (HasValidationAttribute(property, sources))
        {
            return true;
        }

        var name = Regex.Escape(property.PropertyName);
        var comparison = new Regex(
            @"\.\s*" + name + @"(?:\s+is)?\s*(?:<=|>=|<|>)", RegexOptions.None, MatchTimeout);
        // The nullable-option idiom: `options.X is { } local && local <= bound`. The
        // backreference ties the compared local to the extraction, so an extraction that is
        // never compared earns nothing.
        var extracted = new Regex(
            @"\.\s*" + name + @"\s+is\s+\{\s*\}\s+(?<local>\w+)\s*&&\s*\k<local>\s*(?:<=|>=|<|>)",
            RegexOptions.ExplicitCapture, MatchTimeout);
        var throwHelper = new Regex(
            @"ThrowIf\w+\([^)]*\b" + name + @"\b", RegexOptions.None, MatchTimeout);
        var clamp = new Regex(
            @"Math\.Clamp\([^)]*\b" + name + @"\b", RegexOptions.None, MatchTimeout);

        foreach (var (relativePath, text) in sources)
        {
            if (comparison.IsMatch(text) || extracted.IsMatch(text) ||
                throwHelper.IsMatch(text) || clamp.IsMatch(text))
            {
                return true;
            }

            // The self-validating-setter shape: the checked identifier is `value`, so the only
            // textual tie to the property is the throw message's nameof — accepted from the
            // declaring file alone, where nameof(X) is overwhelmingly a validation message.
            if (string.Equals(relativePath, property.RelativePath, StringComparison.Ordinal) &&
                text.Contains($"nameof({property.PropertyName})", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A validation attribute on the declaration counts only when the declaring file also has
    /// a class-level <c>[Validate]</c> — without it the ZeroAlloc generator emits nothing and
    /// the attribute is decoration. Whether the generated validator is then actually invoked in
    /// production is <see cref="InertValidatorGuardTests"/>' subject, not re-checked here.
    /// </summary>
    /// <param name="property">The constrained property.</param>
    /// <param name="sources">Every production source file, comments stripped.</param>
    /// <returns>Whether the property carries a live validation attribute.</returns>
    private static bool HasValidationAttribute(
        ConstrainedProperty property, IReadOnlyList<(string RelativePath, string Text)> sources)
    {
        if (!ValidationAttribute().IsMatch(property.Declaration))
        {
            return false;
        }

        foreach (var (relativePath, text) in sources)
        {
            if (string.Equals(relativePath, property.RelativePath, StringComparison.Ordinal))
            {
                return text.Contains("[Validate]", StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>Matches a property declaration line and captures the property name.</summary>
    /// <remarks>
    /// The ender set — <c>{</c>, <c>=&gt;</c>, or end of line — is what excludes methods (name
    /// followed by <c>(</c>) and fields (name followed by <c>=</c>), and what admits the
    /// three property shapes: auto (<c>{ get; }</c>), expression-bodied, and a bare
    /// <c>public int X</c> line whose accessor block opens on the next line.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+(?:[\w\.\?\[\]<>,]+\s+)+?(?<name>\w+)\s*(?:\{|=>|$)",
        RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PropertyDeclaration();

    /// <summary>Matches a type declaration and captures the type name.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"(?:^|\s)(?:class|record|struct|interface|enum)\s+(?<name>\w+)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex TypeDeclaration();

    /// <summary>
    /// Matches one documented numeric-constraint phrase. Every alternation branch is a shape
    /// that exists in the tree today; <see cref="TheScanSeesEveryShapeAConstraintClaimTakes"/>
    /// pins one live example per family.
    /// </summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"must be greater than|must be at least|must be smaller than|must be less than" +
        @"|must be positive|must be zero or positive|must be non-negative|must not be negative" +
        @"|must not exceed|must be in the range|must be between|must be within" +
        @"|must be [<>]=? ?\d|range:? ?\d[\d.]*\s*[–—-]\s*\d" +
        @"|\(\d[\d.]*\s*[–—-]\s*\d[\d.]*\)|in \[\d[\d.]*, ?\d[\d.]*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConstraintClaim();

    /// <summary>Matches a ZeroAlloc numeric validation attribute on a property declaration.</summary>
    /// <remarks>
    /// The <c>OrEqualTo</c> forms are spelled out because the alternation requires an opening
    /// parenthesis immediately after the name: the earlier <c>GreaterThanOrEqual</c> branch
    /// could never match ZeroAlloc's actual <c>[GreaterThanOrEqualTo(…)]</c> — the stray
    /// <c>To</c> broke it — so a property enforced that way earned no attribute credit until
    /// this was widened (the same widening <c>[InclusiveBetween]</c> once needed).
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"\[(?:GreaterThan|GreaterThanOrEqualTo|LessThan|LessThanOrEqualTo|InclusiveBetween|ExclusiveBetween|Range|InRange|Between|Min|Max)\(",
        RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidationAttribute();

    /// <summary>One property whose doc comment states a numeric constraint.</summary>
    /// <param name="RelativePath">The declaring file, relative to the repository root.</param>
    /// <param name="Line">The declaration's 1-based line.</param>
    /// <param name="Key">The ledger key, <c>TypeName.PropertyName</c>.</param>
    /// <param name="PropertyName">The property name alone, for evidence matching.</param>
    /// <param name="Claim">The matched constraint phrase, for failure messages.</param>
    /// <param name="Declaration">The declaration text including its attributes.</param>
    private sealed record ConstrainedProperty(
        string RelativePath, int Line, string Key, string PropertyName, string Claim, string Declaration);
}
