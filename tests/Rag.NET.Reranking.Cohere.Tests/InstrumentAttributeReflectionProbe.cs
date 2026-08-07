using Rag.NET.Abstractions;
using Xunit;

namespace Rag.NET.Reranking.Cohere.Tests;

/// <summary>
/// Guards the one sharp edge the telemetry pilot introduces.
/// </summary>
/// <remarks>
/// <c>Rag.NET.Abstractions</c> references <c>ZeroAlloc.Telemetry</c> with
/// <c>PrivateAssets="all"</c>, which keeps it out of the published nuspec — verified — and also
/// keeps the assembly out of the build output entirely. So <see cref="IReranker"/> carries
/// <c>[Instrument]</c>/<c>[Trace]</c> attributes whose types cannot be resolved at runtime by
/// anyone who did not reference the package themselves.
/// <para>
/// That is fine for normal use: DI, method dispatch and the generated proxy never inspect
/// attributes. It is not fine if something reflects over the interface — attribute enumeration
/// throws when a type cannot be loaded, and a consumer scanning assemblies would get an error
/// from a package that lists no such dependency, which is close to undebuggable.
/// </para>
/// <para>
/// This project does not reference ZeroAlloc.Telemetry, so it stands in for a consumer. If this
/// test starts failing, reflection over the annotated interfaces has become unsafe and the
/// <c>PrivateAssets</c> decision needs revisiting.
/// </para>
/// </remarks>
public class InstrumentAttributeReflectionProbe
{
    [Fact]
    public void ReflectingOverIReranker_DoesNotThrow()
    {
        var exception = Record.Exception(() => typeof(IReranker).GetCustomAttributes(inherit: false));

        Assert.Null(exception);
    }

    [Fact]
    public void ReflectingOverRerankAsync_DoesNotThrow()
    {
        var method = typeof(IReranker).GetMethod(nameof(IReranker.RerankAsync));
        Assert.NotNull(method);

        var exception = Record.Exception(() => method.GetCustomAttributes(inherit: false));

        Assert.Null(exception);
    }
}
