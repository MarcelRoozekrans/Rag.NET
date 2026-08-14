using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// A <see cref="RagBuilder"/> obtained the only supported way — from inside
/// <c>AddRagNet</c>'s <c>configure</c> delegate.
/// </summary>
/// <remarks>
/// These tests used to write <c>new RagBuilder(new ServiceCollection())</c>, which compiles and
/// which nothing in the product produces. It also has no pipelines in it, so once
/// <c>UseGraphRag</c> and <c>UseMindMapExtraction</c> started placing their behaviours rather
/// than only registering them (issue #191), that shortcut was exercising the one arrangement
/// where placement is impossible. This gives every test the container a user actually has.
/// </remarks>
internal static class ConfiguredRagBuilder
{
    /// <summary>Runs <c>AddRagNet</c> and hands back the builder it configured.</summary>
    /// <returns>A builder whose <c>Services</c> already hold both pipeline builders.</returns>
    public static RagBuilder Create()
    {
        RagBuilder? captured = null;
        new ServiceCollection().AddRagNet(rag => captured = rag);
        return captured!;
    }
}
