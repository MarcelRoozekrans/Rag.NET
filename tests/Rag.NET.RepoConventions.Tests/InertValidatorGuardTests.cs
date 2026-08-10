using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that no validator is inert: every generated <c>&lt;Type&gt;Validator</c> (from a
/// <c>[Validate]</c> class) and every manual <c>Validate*</c>/<c>IsValid*</c> method declared
/// under <c>src/</c> must have at least one production call site — a call reachable only from
/// tests protects nothing while looking like protection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Origin.</b> The issue #90 audit of the configuration surface found validation that
/// existed and ran nowhere:
/// <c>Rag.NET.Embeddings.Onnx/TokenWindowStitcher.ValidateCoverage</c> exists precisely to
/// prevent silently zero-filled embedding rows, and its only caller was <c>Stitch</c> — which
/// production never invokes, because <c>OnnxTokenEmbeddingGenerator</c> folds windows in with
/// <c>CopyWindow</c> directly. The check was green in every test run and absent from every
/// production run. That branch fixed it by calling <c>ValidateCoverage</c> on the production
/// path; this guard exists so the next inert validator is caught when it is written, not by
/// the next audit.
/// </para>
/// <para>
/// <b>How it checks.</b> Comment lines are stripped first — evidence must be code that runs,
/// never prose about it. A <c>[Validate]</c> class's generated validator counts as invoked
/// when <c>&lt;Type&gt;Validator</c> appears anywhere under <c>src/</c> (the generated class
/// itself lives in build output, which the scan skips, so any remaining mention is a use). A
/// manual validation method counts as called when its name is invoked on a line under
/// <c>src/</c> that is not its own declaration. A method marked <c>[CustomValidation]</c> in
/// a <c>[Validate]</c> file is exempt from the manual-method gate: its caller is the generated
/// validator itself, which lives in build output the scan skips — and whether <em>that</em>
/// validator runs in production is
/// <see cref="EveryGeneratedValidatorIsInvokedInProduction"/>'s subject, so the check is not
/// lost, only attributed where it belongs.
/// </para>
/// <para>
/// <b>What this deliberately does NOT catch — do not mistake it for coverage.</b> It sees
/// direct call sites only: the <c>ValidateCoverage</c> defect above was one hop deeper — a
/// production-code caller that was itself test-only — and this guard would have passed it;
/// only the audit found it. Same-named methods share credit (three <c>ValidateOutputShape</c>
/// declarations and several bare <c>Validate</c> methods exist, and any call to the name
/// credits all of them), a call site inside a string literal would count, and nothing here
/// checks that a validator validates the right thing — the bug that started issue #90,
/// <c>ChunkingOptions.Overlap</c>, had a validator that ran in production and enforced a bound
/// that was semantically wrong, in perfect agreement with its doc comment. A validator with a
/// production caller can still be a wrong validator.
/// </para>
/// </remarks>
public sealed partial class InertValidatorGuardTests
{
    /// <summary>
    /// There are 3 <c>[Validate]</c> classes under <c>src/</c> today. Zero found means the
    /// scan lost the tree (or the attribute), and a validator guard that scans nothing passes
    /// for the wrong reason.
    /// </summary>
    private const int FewestPlausibleValidateTypes = 2;

    /// <summary>
    /// There are 42 manual <c>Validate*</c>/<c>IsValid*</c> method declarations under
    /// <c>src/</c> today. Far fewer means the declaration matcher regressed.
    /// </summary>
    private const int FewestPlausibleValidationMethods = 25;

    /// <summary>Longest a single call-site regex may run before the scan is declared broken.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Validators allowed to have no production call site, each with the reason and the owning
    /// work. An inert validator without an entry fails its gate; an entry whose validator gains
    /// a caller — or disappears — fails
    /// <see cref="EveryLedgeredInertValidatorIsStillInert"/> until it is deleted. Keys are the
    /// generated validator's type name, or <c>relative/path.cs:MethodName</c> for a manual
    /// method.
    /// </summary>
    private static readonly Dictionary<string, string> ValidatorsAllowedToHaveNoProductionCaller =
        new(StringComparer.Ordinal)
        {
            // Empty on purpose: when this guard was written every validator had a production
            // caller — which is exactly the moment to pin the invariant, so it stays true. The
            // one candidate, TokenWindowStitcher.ValidateCoverage, was fixed (called from the
            // production stitching path) rather than recorded here; see the type remarks.
        };

    [Fact]
    public void TheScanFindsTheValidationSurface()
    {
        var inventory = ScanProduction();

        Assert.True(
            inventory.ValidateTypes.Count >= FewestPlausibleValidateTypes,
            $"Found only {inventory.ValidateTypes.Count} [Validate] classes under src/, " +
            $"expected at least {FewestPlausibleValidateTypes}. A guard that scans nothing " +
            "passes for the wrong reason, so this fails instead.");

        Assert.True(
            inventory.Methods.Count >= FewestPlausibleValidationMethods,
            $"Found only {inventory.Methods.Count} Validate*/IsValid* method declarations " +
            $"under src/, expected at least {FewestPlausibleValidationMethods}. Either the " +
            "validation surface moved or the declaration matcher regressed.");
    }

    /// <summary>
    /// Pins every shape the validation surface takes, one live example per shape. Losing a
    /// shape narrows the scan silently — the caller tests would still pass, over fewer
    /// validators — so each shape is held by name here instead.
    /// </summary>
    /// <param name="kind">Which inventory the subject must appear in.</param>
    /// <param name="subject">The validator or method the scan must find, with a caller.</param>
    /// <param name="shape">How the shape is written, for the failure message.</param>
    [Theory]
    [InlineData("generated", "ChunkingOptionsValidator", "a [Validate] class's generated validator, invoked in PipelineIngestor")]
    [InlineData("method", "ValidateRequest", "a private method called from its own file")]
    [InlineData("method", "IsValid", "an IsValid-shaped check called from another file (WebhookSignatureValidator)")]
    [InlineData("method", "ValidateCoverage", "an internal method whose production call was issue #90's fix")]
    public void TheScanSeesEveryShapeTheValidationSurfaceTakes(string kind, string subject, string shape)
    {
        var inventory = ScanProduction();
        var found = string.Equals(kind, "generated", StringComparison.Ordinal)
            ? HasValidateType(inventory, subject)
            : HasMethod(inventory, subject);

        Assert.True(
            found,
            $"The scan no longer finds '{subject}', which exists as {shape}. That shape has " +
            "stopped being scanned, so every validator written that way is now unguarded — " +
            "widen the scan back rather than updating this example.");

        Assert.True(
            HasProductionCallSite(subject, inventory),
            $"The scan no longer sees a production call site for '{subject}' ({shape}), " +
            "though one exists. The call-site detection has regressed — every validator is " +
            "about to fail falsely, so fix the detection rather than ledgering the fallout.");
    }

    /// <summary>
    /// Pins the <c>[CustomValidation]</c> exemption from both sides, on live examples: the
    /// marking must be seen where it exists (otherwise <c>ValidateOverlapFitsChunk</c> — whose
    /// only caller is the generated <c>ChunkingOptionsValidator</c> in build output — fails the
    /// manual-method gate falsely), and it must not leak onto ordinary methods (otherwise the
    /// gate goes vacuously green over an exempted inventory).
    /// </summary>
    [Fact]
    public void TheScanSeesCustomValidationMethodsAsGeneratorInvoked()
    {
        var inventory = ScanProduction();

        Assert.Contains(inventory.Methods, m =>
            string.Equals(m.Name, "ValidateOverlapFitsChunk", StringComparison.Ordinal) && m.GeneratorInvoked);

        Assert.Contains(inventory.Methods, m =>
            string.Equals(m.Name, "ValidateRequest", StringComparison.Ordinal) && !m.GeneratorInvoked);
    }

    [Fact]
    public void EveryGeneratedValidatorIsInvokedInProduction()
    {
        var inventory = ScanProduction();

        foreach (var (typeName, relativePath) in inventory.ValidateTypes)
        {
            var validator = typeName + "Validator";
            if (ValidatorsAllowedToHaveNoProductionCaller.ContainsKey(validator))
            {
                continue;
            }

            Assert.True(
                HasProductionCallSite(validator, inventory),
                $"{relativePath} marks {typeName} with [Validate], so the ZeroAlloc generator " +
                $"emits {validator} — and nothing under src/ ever mentions it. A generated " +
                "validator no production code invokes enforces nothing; its attributes are " +
                "decoration. Invoke it where the options enter the pipeline (PipelineIngestor " +
                "is the house shape), remove the [Validate] marking if validation is genuinely " +
                $"not wanted, or record the exemption in " +
                $"{nameof(ValidatorsAllowedToHaveNoProductionCaller)} with its reason.");
        }
    }

    [Fact]
    public void EveryManualValidationMethodHasAProductionCallSite()
    {
        var inventory = ScanProduction();

        foreach (var method in inventory.Methods)
        {
            if (method.GeneratorInvoked ||
                ValidatorsAllowedToHaveNoProductionCaller.ContainsKey(method.Key))
            {
                continue;
            }

            Assert.True(
                HasProductionCallSite(method.Name, inventory),
                $"{method.RelativePath}:{method.Line} declares {method.Name} and no line under " +
                "src/ calls it. A validation method only tests reach protects nothing while " +
                "looking like protection — exactly how TokenWindowStitcher.ValidateCoverage " +
                "sat green while the production path skipped it (issue #90). Call it on the " +
                "production path, delete it if the check is genuinely not wanted, or record " +
                $"the exemption in {nameof(ValidatorsAllowedToHaveNoProductionCaller)} with " +
                "its reason.");
        }
    }

    [Fact]
    public void EveryLedgeredInertValidatorIsStillInert()
    {
        // The staleness guard, same discipline as the sibling ledgers: an allow-list nothing
        // re-checks is how a known gap becomes furniture. The moment a listed validator gains
        // a production caller — or stops existing — its entry is a hole in the guard, so it
        // fails here until it is deleted.
        var inventory = ScanProduction();

        foreach (var (key, reason) in ValidatorsAllowedToHaveNoProductionCaller)
        {
            var name = NameFromLedgerKey(key);

            Assert.True(
                HasValidateTypeForValidator(inventory, key) || HasMethodKey(inventory, key),
                $"'{key}' no longer exists under src/, so its entry in " +
                $"{nameof(ValidatorsAllowedToHaveNoProductionCaller)} is stale. Delete the " +
                $"entry. It was recorded because: {reason}");

            Assert.False(
                HasProductionCallSite(name, inventory),
                $"'{key}' now has a production call site, so its entry in " +
                $"{nameof(ValidatorsAllowedToHaveNoProductionCaller)} is stale. Delete the " +
                "entry so the caller gates guard it against regressing. It was recorded " +
                $"because: {reason}");
        }
    }

    private static bool HasValidateType(Inventory inventory, string typeName)
    {
        foreach (var (name, _) in inventory.ValidateTypes)
        {
            if (string.Equals(name + "Validator", typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValidateTypeForValidator(Inventory inventory, string ledgerKey) =>
        !ledgerKey.Contains(':', StringComparison.Ordinal) && HasValidateType(inventory, ledgerKey);

    private static bool HasMethod(Inventory inventory, string name)
    {
        foreach (var method in inventory.Methods)
        {
            if (string.Equals(method.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMethodKey(Inventory inventory, string ledgerKey)
    {
        foreach (var method in inventory.Methods)
        {
            if (string.Equals(method.Key, ledgerKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NameFromLedgerKey(string key)
    {
        var separator = key.LastIndexOf(':');
        return separator < 0 ? key : key[(separator + 1)..];
    }

    /// <summary>
    /// Decides whether any line under <c>src/</c> calls the given name: the name invoked with
    /// an argument list (or, for validator types, referenced as a member access), on a line
    /// that is not its own declaration. <c>nameof(X)</c> never matches (neither an opening
    /// parenthesis nor a dot follows the name there), and comment lines were removed before
    /// the scan.
    /// </summary>
    /// <param name="name">The validator type or method name.</param>
    /// <param name="inventory">The scanned production sources.</param>
    /// <returns>Whether a production call site exists.</returns>
    private static bool HasProductionCallSite(string name, Inventory inventory)
    {
        var call = new Regex(@"(?<!\w)" + Regex.Escape(name) + @"\s*[(.]", RegexOptions.None, MatchTimeout);

        foreach (var (_, lines) in inventory.Sources)
        {
            foreach (var line in lines)
            {
                if (!call.IsMatch(line))
                {
                    continue;
                }

                var declaration = MethodDeclaration().Match(line);
                if (declaration.Success &&
                    string.Equals(declaration.Groups["name"].Value, name, StringComparison.Ordinal))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans every C# file under <c>src/</c>, comments removed, for the validation surface:
    /// <c>[Validate]</c> classes and manual validation-method declarations.
    /// </summary>
    /// <returns>The inventory, with the stripped sources for call-site checks.</returns>
    private static Inventory ScanProduction()
    {
        var inventory = new Inventory([], [], []);

        foreach (var (relativePath, fullPath) in TestProject.EnumerateSourceFiles("src"))
        {
            var lines = StripCommentLines(File.ReadAllLines(fullPath));
            inventory.Sources.Add((relativePath, lines));

            var text = string.Join('\n', lines);
            foreach (Match attribute in ValidateAttribute().Matches(text))
            {
                var type = TypeDeclaration().Match(text, attribute.Index);
                if (type.Success)
                {
                    inventory.ValidateTypes.Add((type.Groups["name"].Value, relativePath));
                }
            }

            CollectMethods(relativePath, lines, ValidateAttribute().IsMatch(text), inventory.Methods);
        }

        return inventory;
    }

    /// <summary>
    /// Collects manual validation-method declarations, marking each whose immediately preceding
    /// attribute lines carry <c>[CustomValidation]</c> in a <c>[Validate]</c> file — those are
    /// invoked by the generated validator (build output the scan skips), so the manual-method
    /// gate must not demand a textual call site for them. Blank lines (including stripped
    /// comments) and further attribute lines keep the marking attached; any other code line
    /// breaks it, so an unattributed method never inherits the exemption.
    /// </summary>
    /// <param name="relativePath">The file, relative to the repository root.</param>
    /// <param name="lines">The file's comment-stripped lines.</param>
    /// <param name="fileHasValidate">Whether the file marks a type with <c>[Validate]</c>.</param>
    /// <param name="methods">Receives each declaration found.</param>
    private static void CollectMethods(
        string relativePath, string[] lines, bool fileHasValidate, List<ValidationMethod> methods)
    {
        var pendingCustomValidation = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var declaration = MethodDeclaration().Match(lines[i]);
            if (declaration.Success)
            {
                methods.Add(new ValidationMethod(
                    relativePath, i + 1, declaration.Groups["name"].Value,
                    pendingCustomValidation && fileHasValidate));
                pendingCustomValidation = false;
                continue;
            }

            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('['))
            {
                pendingCustomValidation |= trimmed.Contains("[CustomValidation]", StringComparison.Ordinal);
            }
            else if (trimmed.Length > 0)
            {
                pendingCustomValidation = false;
            }
        }
    }

    private static string[] StripCommentLines(string[] lines)
    {
        var stripped = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            stripped[i] = lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                ? string.Empty
                : lines[i];
        }

        return stripped;
    }

    /// <summary>Matches the class-level ZeroAlloc <c>[Validate]</c> marking, exactly.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"\[Validate\]", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ValidateAttribute();

    /// <summary>Matches the type declaration following a <c>[Validate]</c> marking.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"(?:class|record|struct)\s+(?<name>\w+)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex TypeDeclaration();

    /// <summary>
    /// Matches a manual validation-method declaration: an access modifier at line start, then a
    /// signature whose method name begins with <c>Validate</c> or <c>IsValid</c>. The
    /// <c>[^=;{]</c> run keeps field initialisers and statements from matching.
    /// </summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(
        @"^\s*(?:public|private|internal|protected)[^=;{]*\s(?<name>(?:Validate|IsValid)\w*)\s*(?:<[^(>]*>)?\s*\(",
        RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MethodDeclaration();

    /// <summary>One manual validation-method declaration found by the scan.</summary>
    /// <param name="RelativePath">The declaring file, relative to the repository root.</param>
    /// <param name="Line">The declaration's 1-based line.</param>
    /// <param name="Name">The method name.</param>
    /// <param name="GeneratorInvoked">
    /// Whether the method is <c>[CustomValidation]</c>-marked in a <c>[Validate]</c> file, so
    /// its caller is the generated validator rather than a line the scan can see.
    /// </param>
    private sealed record ValidationMethod(string RelativePath, int Line, string Name, bool GeneratorInvoked)
    {
        /// <summary>Gets the ledger key, <c>RelativePath:Name</c>.</summary>
        public string Key => $"{RelativePath}:{Name}";
    }

    /// <summary>What the scan of <c>src/</c> found.</summary>
    /// <param name="ValidateTypes">Each <c>[Validate]</c> class with its declaring file.</param>
    /// <param name="Methods">Every manual validation-method declaration.</param>
    /// <param name="Sources">Every source file's comment-stripped lines.</param>
    private sealed record Inventory(
        List<(string TypeName, string RelativePath)> ValidateTypes,
        List<ValidationMethod> Methods,
        List<(string RelativePath, string[] Lines)> Sources);
}
