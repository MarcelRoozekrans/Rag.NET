using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers;
using Rag.NET.Parsers.Email;
using Rag.NET.Tests.DependencyInjection.StubParsers;
using Xunit;
using EmailPackageParser = Rag.NET.Parsers.Email.EmailDocumentParser;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Two parsers claiming one content type is a startup error rather than registration-order
/// roulette. <c>message/rfc822</c> was Phase 3.11's motivating example — <c>UseEmailChunking()</c>
/// and <c>AddEmailParser()</c> both claimed it — until this phase's Task 5 retired the Templates
/// parser outright, so that pairing is unconditionally legal now (see
/// <see cref="TheEmailChunkingTemplateAndTheEmailParserPackageTogether_DoNotThrow"/>). The guard
/// itself still exists for any collision that remains, pinned below against stub parsers.
/// </summary>
public class ParserClaimValidationTests
{
    private const string SharedContentType = "application/x-shared-name";
    private const string CsvContentType = "text/csv";

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    /// <summary>
    /// A guard that fires on the normal case is worse than no guard. Each package alone must
    /// register cleanly.
    /// </summary>
    [Fact]
    public void TheEmailParserPackageAlone_DoesNotThrow()
    {
        var sp = BaseServices().AddRagNet(rag => rag.AddEmailParser()).BuildServiceProvider();
        Assert.Contains(sp.GetServices<IDocumentParser>(), p => p is EmailPackageParser);
    }

    /// <summary>
    /// Task 5 retired <c>EmailTemplateDocumentParser</c>, so <c>UseEmailChunking()</c> alone
    /// registers only the chunking strategy — no <see cref="ParserClaim"/>, no parser.
    /// </summary>
    [Fact]
    public void TheEmailChunkingTemplateAlone_DoesNotThrow()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.Contains(sp.GetServices<IDocumentChunkingStrategy>(), s => s is EmailChunkingStrategy);
        Assert.DoesNotContain(
            sp.GetServices<ParserClaim>(),
            c => string.Equals(c.RegistrationMethod, "UseEmailChunking()", StringComparison.Ordinal));
    }

    /// <summary>
    /// The collision this file used to be built around. <c>EmailTemplateDocumentParser</c> and
    /// <c>Rag.NET.Parsers.Email</c>'s <c>EmailDocumentParser</c> both claimed <c>message/rfc822</c>
    /// until Task 5 retired the former outright, so pairing <c>UseEmailChunking()</c> with
    /// <c>AddEmailParser()</c> no longer needs the <c>registerParser</c> escape hatch that used to
    /// make it possible — it is simply legal now.
    /// </summary>
    [Fact]
    public void TheEmailChunkingTemplateAndTheEmailParserPackageTogether_DoNotThrow()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is EmailPackageParser);
        Assert.Contains(sp.GetServices<IDocumentChunkingStrategy>(), s => s is EmailChunkingStrategy);
    }

    [Fact]
    public void TheQAPairsTemplateAlone_DoesNotThrow()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseQAPairsChunking()).BuildServiceProvider();
        Assert.Contains(sp.GetServices<IDocumentParser>(), p => p is QAPairsDocumentParser);
    }

    /// <summary>
    /// After Phase 3.11 removed <c>application/octet-stream</c> from the Templates parsers,
    /// <c>UseQAPairsChunking()</c> and <c>AddEmailParser()</c> no longer share a content type at
    /// all. Registering them together must stay legal.
    /// </summary>
    [Fact]
    public void QAPairsAndTheEmailParserPackageTogether_DoNotThrow()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.UseQAPairsChunking();
                rag.AddEmailParser();
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is QAPairsDocumentParser);
        Assert.Contains(parsers, p => p is EmailPackageParser);
    }

    /// <summary>
    /// The conflict message has to offer a way out that the API actually permits, and only for the
    /// claim that declares one. The retired email collision used to demonstrate this against real
    /// production parsers; pinned here against stub parsers instead, now that no production
    /// collision exercises it — <c>UseQAPairsChunking()</c> still declares an opt-out, but its
    /// override mechanism removes the collision before the validator ever runs, so it never reaches
    /// this code path either.
    /// </summary>
    [Fact]
    public void TheConflictMessageNamesTheDeclaredOptOutAndOnlyThatOne()
    {
        const string optOut = "AddParser<FakeCsvParser>(registerParser: false)";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaseServices().AddRagNet(rag =>
            {
                rag.AddParser<FakeCsvParser>();
                rag.Services.AddSingleton(ParserClaim.For<FakeCsvParser>(
                    CsvContentType, "AddParser<FakeCsvParser>()", optOut));

                rag.AddParser<SecondFakeCsvParser>();
                rag.Services.AddSingleton(ParserClaim.For<SecondFakeCsvParser>(
                    CsvContentType, "AddParser<SecondFakeCsvParser>()"));
            }));

        Assert.Contains(optOut, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(ex.Message, "to keep that registration without its parser"));
    }

    /// <summary>
    /// The same flag on <c>UseQAPairsChunking()</c>. Nothing in this repository collides with its
    /// claims today, but the guard fires on any duplicated claim — a third-party CSV parser
    /// included — and an escape hatch present on one bundling registration and absent from its twin
    /// is a trap rather than a convenience.
    /// </summary>
    [Fact]
    public void TheQAPairsParserOptOut_DropsOnlyTheParser()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseQAPairsChunking(registerParser: false))
            .BuildServiceProvider();

        Assert.DoesNotContain(sp.GetServices<IDocumentParser>(), p => p is QAPairsDocumentParser);
        Assert.Contains(sp.GetServices<IDocumentChunkingStrategy>(), s => s is QAPairsChunkingStrategy);
    }

    /// <summary>
    /// The built-in parsers are claimants like any other. <c>AddRagNETServices()</c> registers
    /// <see cref="TextDocumentParser"/> before <c>configure</c> runs, so a user parser claiming
    /// <c>text/plain</c> loses the race and never runs — and until the built-ins declared claims
    /// the guard could not see it, because one declared claimant is not a conflict.
    /// </summary>
    [Fact]
    public void AUserParserClaimingTextPlain_ConflictsWithTheBuiltInTextParser()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.AddParser<PlainTextStubParser>();
                rag.Services.AddSingleton(ParserClaim.For<PlainTextStubParser>(
                    "text/plain", "AddParser<PlainTextStubParser>()"));
            }));

        Assert.Contains("text/plain", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(TextDocumentParser).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(PlainTextStubParser).FullName!, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="MarkdownDocumentParser"/> answers <c>text/x-markdown</c> as well as
    /// <c>text/markdown</c>. A claim that declared only the documented spelling would leave the
    /// alias exactly as undetected as declaring nothing at all.
    /// </summary>
    [Fact]
    public void AUserParserClaimingTheMarkdownAlias_ConflictsWithTheBuiltInMarkdownParser()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.AddParser<MarkdownStubParser>();
                rag.Services.AddSingleton(ParserClaim.For<MarkdownStubParser>(
                    "text/x-markdown", "AddParser<MarkdownStubParser>()"));
            }));

        Assert.Contains("text/x-markdown", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(MarkdownDocumentParser).FullName!, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing the built-in parsers claim may fire on a container that adds no parser of its own.
    /// A guard that rejects the default registration is worse than no guard at all.
    /// </summary>
    [Fact]
    public void TheBuiltInParsersAlone_DoNotConflictWithEachOther()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is TextDocumentParser);
        Assert.Contains(parsers, p => p is MarkdownDocumentParser);
    }

    /// <summary>
    /// What holds <see cref="ParserClaim.For{TParser}"/> to <c>FullName</c>. Two <i>distinct</i>
    /// parsers both called <c>SharedNameParser</c>, in different namespaces, claiming one content
    /// type: keyed on the short name they collapse into a single claimant and the guard falls
    /// silent on the collision it exists for.
    /// </summary>
    /// <remarks>
    /// Written because the rule had lost its coverage. It used to be pinned incidentally — the two
    /// colliding parsers in this repository were both named <c>EmailDocumentParser</c>, so keying
    /// on <c>Name</c> reddened four tests — and Phase 3.11's rename removed that accident without
    /// replacing it. A same-named pair arriving from two third-party packages is not something this
    /// repository can rename away, so the rule is pinned directly rather than through a coincidence.
    /// </remarks>
    [Fact]
    public void TwoParsersSharingAShortName_StillConflict()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.AddParser<StubParsers.VendorA.SharedNameParser>();
                rag.AddParser<StubParsers.VendorB.SharedNameParser>();
                rag.Services.AddSingleton(ParserClaim.For<StubParsers.VendorA.SharedNameParser>(
                    SharedContentType, "AddParser<StubParsers.VendorA.SharedNameParser>()"));
                rag.Services.AddSingleton(ParserClaim.For<StubParsers.VendorB.SharedNameParser>(
                    SharedContentType, "AddParser<StubParsers.VendorB.SharedNameParser>()"));
            }));

        Assert.Equal(
            typeof(StubParsers.VendorA.SharedNameParser).Name, typeof(StubParsers.VendorB.SharedNameParser).Name);
        Assert.Contains(SharedContentType, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            typeof(StubParsers.VendorA.SharedNameParser).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            typeof(StubParsers.VendorB.SharedNameParser).FullName!, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registering the same package twice is documented as legal, and declares the same claims
    /// twice. The guard must compare claiming <i>types</i>, not claim count.
    /// </summary>
    [Fact]
    public void TheSamePackageRegisteredTwice_DoesNotThrow()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.AddEmailParser();
                rag.AddEmailParser(o => o.MaxEmbeddedDepth = 1);
            })
            .BuildServiceProvider();

        Assert.Contains(sp.GetServices<IDocumentParser>(), p => p is EmailPackageParser);
    }

    /// <summary>
    /// <c>replaces:</c> must remove both halves of the replaced registration — its
    /// <see cref="IDocumentParser"/> descriptor and its <see cref="ParserClaim"/> — together, not
    /// merely silence the conflict the claim would otherwise trip.
    /// </summary>
    [Fact]
    public void AddParser_WithReplaces_RemovesTheReplacedParserAndItsClaim()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.AddParser<CsvDocumentParser>();
                rag.Services.AddSingleton(ParserClaim.For<CsvDocumentParser>(
                    CsvContentType, "AddParser<CsvDocumentParser>()"));

                rag.AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser));
                rag.Services.AddSingleton(ParserClaim.For<FakeCsvParser>(
                    CsvContentType,
                    "AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser))",
                    replaces: typeof(CsvDocumentParser)));
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is FakeCsvParser);
        Assert.DoesNotContain(parsers, p => p is CsvDocumentParser);
    }

    /// <summary>
    /// The point of the feature, asserted separately from mere clean registration. Selection takes
    /// the first registered parser whose <c>CanParse</c> matches, and <c>CsvDocumentParser</c> is
    /// registered first here — exactly the shape a built-in registered ahead of <c>configure</c>
    /// takes. Without descriptor removal this would still pass the claim check and still lose.
    /// </summary>
    [Fact]
    public void AddParser_WithReplaces_MakesTheReplacementWinSelection()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.AddParser<CsvDocumentParser>();
                rag.Services.AddSingleton(ParserClaim.For<CsvDocumentParser>(
                    CsvContentType, "AddParser<CsvDocumentParser>()"));

                rag.AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser));
                rag.Services.AddSingleton(ParserClaim.For<FakeCsvParser>(
                    CsvContentType,
                    "AddParser<FakeCsvParser>(replaces: typeof(CsvDocumentParser))",
                    replaces: typeof(CsvDocumentParser)));
            })
            .BuildServiceProvider();

        var selected = sp.GetServices<IDocumentParser>().First(p => p.CanParse(CsvContentType));

        Assert.IsType<FakeCsvParser>(selected);
    }

    /// <summary>
    /// The escape hatch must not become a way to switch the guard off entirely. Two parsers
    /// claiming the same content type without <c>replaces:</c> must still throw.
    /// </summary>
    [Fact]
    public void AddParser_WithoutReplaces_StillConflictsWhenBothDeclare()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaseServices().AddRagNet(rag =>
            {
                rag.AddParser<FakeCsvParser>();
                rag.Services.AddSingleton(ParserClaim.For<FakeCsvParser>(
                    CsvContentType, "AddParser<FakeCsvParser>()"));

                rag.AddParser<SecondFakeCsvParser>();
                rag.Services.AddSingleton(ParserClaim.For<SecondFakeCsvParser>(
                    CsvContentType, "AddParser<SecondFakeCsvParser>()"));
            }));

        Assert.Contains(CsvContentType, ex.Message, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
