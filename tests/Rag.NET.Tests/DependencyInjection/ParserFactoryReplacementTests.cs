using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// A factory registration does not reveal the type it builds, so <c>replaces:</c> cannot find it
/// by <c>ImplementationType</c>. <c>AddParserFactory</c> records a <see cref="ParserRegistration"/>
/// marker that makes it reachable; an unmarked factory parser is a loud error rather than a no-op.
/// </summary>
public class ParserFactoryReplacementTests
{
    private sealed class FakeParser : IDocumentParser
    {
        public bool CanParse(string contentType) => string.Equals(contentType, "x/y", StringComparison.Ordinal);

        public IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream, DocumentMetadata metadata, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<DocumentSection>();
    }

    private sealed class OtherParser : IDocumentParser
    {
        public bool CanParse(string contentType) => string.Equals(contentType, "x/y", StringComparison.Ordinal);

        public IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream, DocumentMetadata metadata, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<DocumentSection>();
    }

    private static (ServiceCollection Services, RagBuilder Builder) Create()
    {
        var services = new ServiceCollection();
        return (services, new RagBuilder(services));
    }

    private static void RegisterFakeWithClaim(RagBuilder builder)
    {
        builder.AddParserFactory(_ => new FakeParser());
        builder.Services.AddSingleton(ParserClaim.For<FakeParser>("x/y", "AddFake()"));
    }

    [Fact]
    public void AddParserFactory_RegistersTheParserAndAMarker()
    {
        var (services, builder) = Create();

        builder.AddParserFactory(_ => new FakeParser());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IDocumentParser));
        Assert.NotNull(descriptor.ImplementationFactory);
        var marker = Assert.IsType<ParserRegistration>(
            Assert.Single(services, d => d.ServiceType == typeof(ParserRegistration)).ImplementationInstance);
        Assert.Equal(typeof(FakeParser).FullName, marker.ParserTypeName);
        Assert.Same(descriptor, marker.Descriptor);
    }

    [Fact]
    public void AddParser_WithReplaces_RemovesAFactoryRegisteredParser()
    {
        var (services, builder) = Create();
        RegisterFakeWithClaim(builder);

        builder.AddParser<OtherParser>(replaces: typeof(FakeParser));

        AssertFakeGone(services);
    }

    [Fact]
    public void AddParser_WithReplacesTypeNames_RemovesAFactoryRegisteredParser()
    {
        var (services, builder) = Create();
        RegisterFakeWithClaim(builder);

        builder.AddParser<OtherParser>(replacesTypeNames: [typeof(FakeParser).FullName!]);

        AssertFakeGone(services);
    }

    private static void AssertFakeGone(ServiceCollection services)
    {
        using var sp = services.BuildServiceProvider();
        Assert.DoesNotContain(sp.GetServices<IDocumentParser>(), p => p is FakeParser);
        Assert.Contains(sp.GetServices<IDocumentParser>(), p => p is OtherParser);
        Assert.DoesNotContain(
            services.Where(d => d.ServiceType == typeof(ParserRegistration)),
            d => string.Equals(((ParserRegistration)d.ImplementationInstance!).ParserTypeName, typeof(FakeParser).FullName, StringComparison.Ordinal));
        Assert.DoesNotContain(
            services.Where(d => d.ServiceType == typeof(ParserClaim)),
            d => string.Equals(((ParserClaim)d.ImplementationInstance!).ParserTypeName, typeof(FakeParser).FullName, StringComparison.Ordinal));
    }

    [Fact]
    public void AddParserFactory_WithReplaces_MakesTheReplacementWinSelection()
    {
        var (services, builder) = Create();
        builder.AddParser<OtherParser>();

        builder.AddParserFactory(_ => new FakeParser(), replaces: typeof(OtherParser));

        using var sp = services.BuildServiceProvider();
        var parsers = sp.GetServices<IDocumentParser>().ToList();
        Assert.Contains(parsers, p => p is FakeParser);
        Assert.DoesNotContain(parsers, p => p is OtherParser);
    }

    [Fact]
    public void Replace_UnmarkedFactoryParserWithClaims_Throws()
    {
        var (services, builder) = Create();
        services.AddSingleton<IDocumentParser>(_ => new FakeParser());
        services.AddSingleton(ParserClaim.For<FakeParser>("x/y", "AddFake()"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddParser<OtherParser>(replaces: typeof(FakeParser)));

        Assert.Contains(typeof(FakeParser).FullName!, ex.Message);
        Assert.Contains("AddParserFactory", ex.Message);
    }

    [Fact]
    public void Replace_NameMatchingNothing_IsANoOp()
    {
        var (services, builder) = Create();
        RegisterFakeWithClaim(builder);
        var before = services.Count;

        builder.AddParser<OtherParser>(replacesTypeNames: ["Not.Installed.Parser"]);

        Assert.Equal(before + 1, services.Count);
    }
}
