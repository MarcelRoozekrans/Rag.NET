using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>One service type <c>AddRagNetShared</c> declared, and enough about its registration
/// for <c>BuildFactory</c> to decide whether it can actually be forwarded.</summary>
/// <param name="ServiceType">The declared service type.</param>
/// <param name="Lifetime">The lifetime the declaring descriptor was registered with.</param>
/// <param name="IsKeyed">Whether the declaring descriptor is a keyed service.</param>
/// <remarks>
/// A bare <see cref="Type"/> is not enough: <c>AddRagNetShared(rag =&gt;
/// rag.Services.AddHttpClient(...))</c> alone declares 25+ descriptors, several of them open
/// generics (<c>IOptions&lt;&gt;</c>) or non-singletons (a transient <c>HttpClient</c>, a scoped
/// <c>IOptionsSnapshot&lt;&gt;</c>). Forwarding either kind blindly either crashes
/// (<c>GetRequiredService</c> cannot construct an open generic from a closed implementation) or
/// silently freezes a non-singleton into a singleton for every pipeline. Both must be excluded,
/// not just recorded.
/// </remarks>
internal readonly record struct SharedServiceEntry(Type ServiceType, ServiceLifetime Lifetime, bool IsKeyed);
