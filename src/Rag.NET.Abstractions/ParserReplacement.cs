using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Abstractions;

/// <summary>
/// The removal behind every <c>replaces:</c>, shared by <c>RagBuilder.AddParser</c> and
/// <see cref="ParserFactoryRegistrationExtensions.AddParserFactory{TParser}"/>.
/// </summary>
internal static class ParserReplacement
{
    /// <summary>
    /// Removes every parser named by <paramref name="replaces"/> and <paramref name="replacesTypeNames"/>,
    /// or removes nothing and throws. All names are validated before any is removed, so a failing
    /// call leaves <paramref name="services"/> untouched. <paramref name="replaces"/> is matched by
    /// <see cref="Type.FullName"/>, not <see cref="Type.Name"/>; see
    /// <see cref="ParserClaim.ParserTypeName"/> for why.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A name has <see cref="ParserClaim"/>s but no findable <see cref="IDocumentParser"/>
    /// registration: the parser declared what it claims, so it is installed, but its registration is
    /// an unmarked factory that cannot be found. Silently leaving it would let it keep winning
    /// selection.
    /// </exception>
    internal static void Remove(IServiceCollection services, Type? replaces, string[]? replacesTypeNames)
    {
        var names = new List<string>();
        if (replaces is not null)
        {
            names.Add(replaces.FullName ?? replaces.Name);
        }

        if (replacesTypeNames is not null)
        {
            names.AddRange(replacesTypeNames);
        }

        foreach (var name in names)
        {
            if (HasClaim(services, name) && !HasRegistration(services, name))
            {
                throw new InvalidOperationException(
                    $"Cannot replace parser '{name}': it declares content-type claims, so it is " +
                    "installed, but no registration for it could be found. A factory registration does not " +
                    "reveal the type it builds, so the package that registers it must use " +
                    "AddParserFactory<TParser> instead of AddSingleton<IDocumentParser>(sp => ...).");
            }
        }

        foreach (var name in names)
        {
            RemoveNamed(services, name);
        }
    }

    private static bool HasClaim(IServiceCollection services, string name) =>
        services.Any(d => d.ServiceType == typeof(ParserClaim) &&
            d.ImplementationInstance is ParserClaim claim &&
            string.Equals(claim.ParserTypeName, name, StringComparison.Ordinal));

    private static bool HasRegistration(IServiceCollection services, string name) =>
        services.Any(d => IsParserNamed(d, name) ||
            (d.ServiceType == typeof(ParserRegistration) &&
                d.ImplementationInstance is ParserRegistration marker &&
                string.Equals(marker.ParserTypeName, name, StringComparison.Ordinal) &&
                services.Contains(marker.Descriptor)));

    /// <summary>
    /// A type registration names its parser through <see cref="ServiceDescriptor.ImplementationType"/>;
    /// an instance registration through the instance's own type. A factory names nothing.
    /// </summary>
    private static bool IsParserNamed(ServiceDescriptor d, string name) =>
        d.ServiceType == typeof(IDocumentParser) &&
        string.Equals(
            d.ImplementationType?.FullName ?? d.ImplementationInstance?.GetType().FullName,
            name,
            StringComparison.Ordinal);

    private static void RemoveNamed(IServiceCollection services, string name)
    {
        // Collected first, removed after: a marker points at a descriptor that can sit anywhere in
        // the collection, so removing while scanning by index would skip or misplace entries.
        var doomed = new List<ServiceDescriptor>();
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(ParserRegistration) &&
                d.ImplementationInstance is ParserRegistration marker &&
                string.Equals(marker.ParserTypeName, name, StringComparison.Ordinal))
            {
                doomed.Add(d);
                doomed.Add(marker.Descriptor);
            }
            else if ((d.ServiceType == typeof(ParserClaim) &&
                    d.ImplementationInstance is ParserClaim claim &&
                    string.Equals(claim.ParserTypeName, name, StringComparison.Ordinal)) ||
                IsParserNamed(d, name))
            {
                doomed.Add(d);
            }
        }

        foreach (var d in doomed)
        {
            services.Remove(d);
        }
    }
}
