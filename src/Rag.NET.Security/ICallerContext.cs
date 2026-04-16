namespace Rag.NET.Security;

/// <summary>
/// Provides the roles of the current caller for RBAC chunk filtering.
/// Implement as a singleton using <c>IHttpContextAccessor</c> (ASP.NET Core)
/// or <c>AsyncLocal&lt;IReadOnlyList&lt;string&gt;&gt;</c> (other hosts).
/// Return an empty list when no caller context is available — RBAC will pass all chunks through.
/// </summary>
public interface ICallerContext
{
    /// <summary>Returns the roles of the current caller.</summary>
    IReadOnlyList<string> GetRoles();
}
