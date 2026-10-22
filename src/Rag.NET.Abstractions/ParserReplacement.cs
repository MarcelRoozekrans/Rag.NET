using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Abstractions;

/// <summary>
/// The removal behind every <c>replaces:</c>, shared by <c>RagBuilder.AddParser</c> and
/// <see cref="ParserFactoryRegistrationExtensions.AddParserFactory{TParser}"/>.
/// </summary>
internal static class ParserReplacement
{
    /// <summary>
    /// Removes the parser named <paramref name="parserTypeName"/>: its <see cref="IDocumentParser"/>
    /// descriptors matched by <see cref="ServiceDescriptor.ImplementationType"/>, every descriptor a
    /// matching <see cref="ParserRegistration"/> marks, the markers, and matching
    /// <see cref="ParserClaim"/>s. A name matching nothing is a silent no-op, which an optional
    /// package that was never installed requires.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// At least one claim was removed but no <see cref="IDocumentParser"/> descriptor: the parser
    /// declared what it claims, so it is installed, but its registration is an unmarked factory
    /// that cannot be found. Silently leaving it would let it keep winning selection.
    /// </exception>
    internal static void Remove(IServiceCollection services, string parserTypeName)
    {
        var removedParser = false;
        var removedClaim = false;

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];

            if (descriptor.ServiceType == typeof(ParserRegistration) &&
                descriptor.ImplementationInstance is ParserRegistration marker &&
                string.Equals(marker.ParserTypeName, parserTypeName, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
                removedParser |= services.Remove(marker.Descriptor);
                // The marked descriptor can sit before or after i; restart the scan from the end.
                i = services.Count;
                continue;
            }

            if (descriptor.ServiceType == typeof(IDocumentParser) &&
                string.Equals(descriptor.ImplementationType?.FullName, parserTypeName, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
                removedParser = true;
                continue;
            }

            if (descriptor.ServiceType == typeof(ParserClaim) &&
                descriptor.ImplementationInstance is ParserClaim claim &&
                string.Equals(claim.ParserTypeName, parserTypeName, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
                removedClaim = true;
            }
        }

        if (removedClaim && !removedParser)
        {
            throw new InvalidOperationException(
                $"Cannot replace parser '{parserTypeName}': it declares content-type claims, so it is " +
                "installed, but no registration for it could be found. A factory registration does not " +
                "reveal the type it builds, so the package that registers it must use " +
                "AddParserFactory<TParser> instead of AddSingleton<IDocumentParser>(sp => ...).");
        }
    }
}
