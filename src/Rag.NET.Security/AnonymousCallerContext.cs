namespace Rag.NET.Security;

/// <summary>Fallback <see cref="ICallerContext"/> that returns no roles. Used when RBAC is not configured.</summary>
internal sealed class AnonymousCallerContext : ICallerContext
{
    public IReadOnlyList<string> GetRoles() => [];
}
