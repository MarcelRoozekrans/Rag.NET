using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Rag.NET.Security;

namespace Rag.NET.Security.AspNetCore;

/// <summary>
/// <see cref="ICallerContext"/> that reads roles from the current ASP.NET Core
/// <see cref="ClaimsPrincipal"/> via <see cref="IHttpContextAccessor"/>.
/// Register as a singleton — <see cref="IHttpContextAccessor"/> handles per-request context via AsyncLocal.
/// Returns an empty list when no HTTP context is available (e.g. background jobs).
/// </summary>
public sealed class ClaimsPrincipalCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public IReadOnlyList<string> GetRoles()
    {
        var claims = accessor.HttpContext?.User.FindAll(ClaimTypes.Role);
        if (claims is null)
            return [];

        var roles = new List<string>();
        foreach (var claim in claims)
            roles.Add(claim.Value);
        return roles;
    }
}
