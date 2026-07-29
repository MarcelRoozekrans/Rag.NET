using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.Templates;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers.Email;
using Xunit;
using EmailPackageParser = Rag.NET.Parsers.Email.EmailDocumentParser;
using TemplatesEmailParser = Rag.NET.Chunking.Templates.EmailDocumentParser;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Two parsers claiming one content type is a startup error rather than registration-order
/// roulette. <c>UseEmailChunking()</c> and <c>AddEmailParser()</c> both claim
/// <c>message/rfc822</c>; parser selection takes the first match, so one of them silently never
/// runs and the content it would have produced is simply absent.
/// </summary>
public class ParserClaimValidationTests
{
    private const string EmlContentType = "message/rfc822";

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void TwoPackagesClaimingOneContentType_ThrowsAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.Contains(EmlContentType, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The order the user registered in must not change the outcome — the whole point of running
    /// the check after <c>configure</c> rather than inside either registration call.
    /// </summary>
    [Fact]
    public void TwoPackagesClaimingOneContentType_ThrowsInEitherOrder()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.AddEmailParser();
                rag.UseEmailChunking();
            }));

        Assert.Contains(EmlContentType, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserted by its parts rather than by its prose, and each part is a name the reader needs in
    /// order to act: which two types collide, and which two calls to go and look at. A Phase 3.9
    /// test asserted the message "names the ceiling" and passed against a mutant, because a second
    /// incidental occurrence of the literal satisfied the substring — so each of these four names
    /// was checked to fail the test when removed from the message.
    /// </summary>
    [Fact]
    public void TheConflictMessageNamesBothParsersAndBothRegistrationCalls()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.Contains(typeof(TemplatesEmailParser).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(EmailPackageParser).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("UseEmailChunking()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddEmailParser()", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The full type name is load-bearing. Both packages export a public <c>EmailDocumentParser</c>,
    /// so a message naming the short name twice would tell the reader nothing about which
    /// registration to remove — and the assertion above would pass against a message that named
    /// only one of them.
    /// </summary>
    [Fact]
    public void TheTwoClaimantsAreDistinguishableInTheMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.NotEqual(typeof(TemplatesEmailParser).FullName, typeof(EmailPackageParser).FullName);
        Assert.Equal(2, CountOccurrences(ex.Message, "registered by "));
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

    [Fact]
    public void TheEmailChunkingTemplateAlone_DoesNotThrow()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseEmailChunking()).BuildServiceProvider();
        Assert.Contains(sp.GetServices<IDocumentParser>(), p => p is TemplatesEmailParser);
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
    /// The conflict message has to offer a way out that the API actually permits.
    /// <c>UseEmailChunking()</c> registers a parser <i>and</i> a chunking strategy, so "register
    /// only one of them" is unfollowable for a user who wants the email-shaped chunking with
    /// <c>Rag.NET.Parsers.Email</c> parsing — that pairing was unreachable until
    /// <c>RegisterParser</c> existed. The message quotes the opt-out verbatim so it can be pasted.
    /// </summary>
    [Fact]
    public void TheConflictMessageNamesTheParserOptOut()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.Contains(
            "UseEmailChunking(o => o.RegisterParser = false)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>AddEmailParser()</c> registers nothing but parsers, so it declares no opt-out and the
    /// message must not invent one for it. Exactly one of the two claimants offers a way out.
    /// </summary>
    [Fact]
    public void OnlyTheBundlingRegistrationOffersAnOptOut()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BaseServices().AddRagNet(rag =>
            {
                rag.UseEmailChunking();
                rag.AddEmailParser();
            }));

        Assert.Equal(1, CountOccurrences(ex.Message, "to keep that registration without its parser"));
        Assert.DoesNotContain(
            "AddEmailParser(o => o.RegisterParser", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-out has to actually resolve the conflict, and has to drop only the parser.
    /// </summary>
    [Fact]
    public void TheEmailParserOptOut_ResolvesTheConflictAndKeepsTheStrategy()
    {
        var sp = BaseServices()
            .AddRagNet(rag =>
            {
                rag.UseEmailChunking(o => o.RegisterParser = false);
                rag.AddEmailParser();
            })
            .BuildServiceProvider();

        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is EmailPackageParser);
        Assert.DoesNotContain(parsers, p => p is TemplatesEmailParser);
        Assert.Contains(sp.GetServices<IDocumentChunkingStrategy>(), s => s is EmailChunkingStrategy);
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
            .AddRagNet(rag => rag.UseQAPairsChunking(o => o.RegisterParser = false))
            .BuildServiceProvider();

        Assert.DoesNotContain(sp.GetServices<IDocumentParser>(), p => p is QAPairsDocumentParser);
        Assert.Contains(sp.GetServices<IDocumentChunkingStrategy>(), s => s is QAPairsChunkingStrategy);
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
