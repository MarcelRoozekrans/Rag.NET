using System.Text.RegularExpressions;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Refuses a committed WireMock cassette that carries anything shaped like a credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>A prerequisite for Phase 6.1, not a nicety.</b> 6.1's design states the requirement directly:
/// "Scrubbing is a correctness property, not hygiene: tokens, cookies, account ids and customer
/// data removed before commit, and a test asserts no committed fixture matches a credential
/// pattern. <b>A leaked token in a fixture is worse than no fixture.</b>" That test did not exist.
/// </para>
/// <para>
/// It matters now because 6.1's remaining work is to <b>re-record ~19 connector cassettes against
/// real services</b>, and the natural way to do that is <c>WIREMOCK_RECORD=true</c> with a real
/// account — which proxies live traffic straight into <c>Cassettes/</c>, <c>Authorization</c>
/// headers and all. Inviting that into a public repository without a guard is inviting a leak. This
/// is the guard.
/// </para>
/// <para>
/// <b>Shape-based, not allow-list based.</b> It looks for the shapes credentials take rather than
/// for known secrets, because the point is to catch the token nobody thought to look for. False
/// positives are the intended failure direction: a cassette that trips this and is genuinely clean
/// costs one reviewer a minute, while a cassette that leaks costs a credential rotation and a
/// history rewrite.
/// </para>
/// </remarks>
public sealed class CassetteSecretTests
{
    /// <summary>
    /// Placeholders the existing hand-written cassettes and fixtures use on purpose. A recorded
    /// cassette must not contain a real credential; it may contain these.
    /// </summary>
    private static readonly string[] KnownPlaceholders =
    [
        "fake-pat", "fake-token", "test-key", "test-token", "fake-key", "dummy", "cassette-key",
        "devstoreaccount1", "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
        "R4gNet!Emulator", "sbemulatorns", "AccountKey=dGVzdA==",
    ];

    /// <summary>
    /// Credential shapes, each with the provider it belongs to so a failure names what to rotate.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] CredentialShapes =
    [
        ("GitHub token", new Regex(@"gh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Slack token", new Regex(@"xox[abposr]-[A-Za-z0-9-]{10,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Notion secret", new Regex(@"secret_[A-Za-z0-9]{32,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Airtable PAT", new Regex(@"pat[A-Za-z0-9]{14,}\.[A-Za-z0-9]{32,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Google OAuth token", new Regex(@"ya29\.[A-Za-z0-9_-]{20,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Google API key", new Regex(@"AIza[A-Za-z0-9_-]{35}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("OpenAI/OpenRouter key", new Regex(@"sk-(?:or-)?[A-Za-z0-9-]{20,}", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))),
        ("Atlassian API token", new Regex(@"ATATT[A-Za-z0-9_\-=]{20,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("AWS access key id", new Regex(@"A(?:KIA|SIA)[0-9A-Z]{16}", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))),
        ("private key block", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("JWT", new Regex(@"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.None, TimeSpan.FromSeconds(2))),
        ("Bearer header value", new Regex(@"""(?i:authorization)""\s*:\s*""(?i:bearer)\s+(?!fake|test|dummy)[A-Za-z0-9._-]{20,}""", RegexOptions.None, TimeSpan.FromSeconds(2))),
        // Azure keys are opaque — 32 hex characters classically, longer since — so there is no
        // prefix to key off the way gh*_ and xox*- allow. Both shapes therefore match on the
        // header NAME instead and treat whatever value sits with it as the credential, which is
        // also why they are the only two entries here that can produce a false positive on an
        // innocent long value. That is the direction this guard errs in on purpose.
        ("Azure key header", new Regex(@"""(?i:ocp-apim-subscription-key|api-key)""\s*:\s*""[^""]{16,}""", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))),
        // And the same key as a *recording* lays it out: the name and the value are separated by
        // the matcher envelope WireMock writes, so the flat shape above cannot see it. Recorded
        // cassettes are the case this guard was built for; it could not read one until now.
        ("Azure key matcher", new Regex(@"""(?i:ocp-apim-subscription-key|api-key)""[\s\S]{0,300}?""Pattern""\s*:\s*""[^""]{16,}""", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))),
    ];

    [Fact]
    public void NoCommittedCassetteCarriesACredential()
    {
        var root = FindRepositoryRoot();
        var cassettes = Directory
            .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(p => p.Replace(Path.DirectorySeparatorChar, '/').Contains("/Cassettes/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Guards the guard: if the discovery ever stops finding cassettes, this test would pass
        // over nothing, silently, forever — the inert-guard shape this repository keeps deleting.
        Assert.True(
            cassettes.Count >= 20,
            $"Only {cassettes.Count} cassettes found under {root}. The scan has lost the tree; it " +
            "would pass over nothing and report success.");

        var failures = new List<string>();
        foreach (var file in cassettes)
        {
            var found = FindCredential(File.ReadAllText(file));
            if (found is not null)
            {
                failures.Add(
                    $"{Path.GetRelativePath(root, file)}: looks like a {found.Value.Name} " +
                    $"({Redact(found.Value.Value)}). Scrub it, and ROTATE THE CREDENTIAL — it is in " +
                    "git history the moment it is committed.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "A committed cassette carries something shaped like a real credential. Scrubbing is a " +
            "correctness property, not hygiene: a leaked token in a fixture is worse than no " +
            "fixture, and re-recording against a live service (WIREMOCK_RECORD=true) proxies " +
            "Authorization headers straight into these files.\n  - " +
            string.Join("\n  - ", failures));
    }

    /// <summary>
    /// The first credential shape <paramref name="text"/> matches, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from the scan above so the shapes can be tested against known-bad and
    /// known-good inputs directly. The scan reads whatever happens to be committed, so on a
    /// clean tree it passes whether the patterns work or not — it can prove a leak, never the
    /// ability to detect one.
    /// </remarks>
    /// <param name="text">A cassette's contents.</param>
    /// <returns>The matching shape's name and the matched text, or <see langword="null"/>.</returns>
    internal static (string Name, string Value)? FindCredential(string text)
    {
        foreach (var (name, pattern) in CredentialShapes)
        {
            var match = pattern.Match(text);
            if (match.Success && !IsPlaceholder(match.Value))
            {
                return (name, match.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// A hand-written cassette carrying an Azure key beside its header name.
    /// </summary>
    [Fact]
    public void FindCredential_DetectsAnAzureKeyHeader()
    {
        const string Cassette = """
            { "Request": { "Headers": { "Ocp-Apim-Subscription-Key": "0123456789abcdef0123456789abcdef" } } }
            """;

        Assert.Equal("Azure key header", FindCredential(Cassette)?.Name);
    }

    /// <summary>
    /// The same key as a recorded cassette carries it: header name, then the value some way
    /// below inside a matcher. The flat shape above does not match this one, and a recording is
    /// the case this guard exists for.
    /// </summary>
    [Fact]
    public void FindCredential_DetectsAnAzureKeyRecordedAsAMatcher()
    {
        const string Cassette = """
            {
              "Request": {
                "Headers": [
                  {
                    "Name": "api-key",
                    "Matchers": [
                      { "Name": "WildcardMatcher", "Pattern": "0123456789abcdef0123456789abcdef" }
                    ]
                  }
                ]
              }
            }
            """;

        Assert.Equal("Azure key matcher", FindCredential(Cassette)?.Name);
    }

    /// <summary>
    /// The placeholder the Document Intelligence suite authenticates with. A guard that fired on
    /// it would fire on every clean run, which is how a guard gets switched off.
    /// </summary>
    [Fact]
    public void FindCredential_IgnoresThePlaceholderKeyTheCassettesUse()
    {
        const string Cassette = """
            { "Request": { "Headers": { "Ocp-Apim-Subscription-Key": "cassette-key-not-a-real-one" } } }
            """;

        Assert.Null(FindCredential(Cassette));
    }

    private static bool IsPlaceholder(string value) =>
        KnownPlaceholders.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows enough to locate the value without reprinting it into CI logs.</summary>
    private static string Redact(string value) =>
        value.Length <= 8 ? "********" : value[..4] + "…" + value[^2..];

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rag.NET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Rag.NET.slnx not found above the test output.");
    }
}
