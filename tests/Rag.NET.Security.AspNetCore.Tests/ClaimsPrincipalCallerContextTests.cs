using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Rag.NET.Security.AspNetCore.Tests;

/// <summary>
/// <see cref="ClaimsPrincipalCallerContext"/> against real ASP.NET Core primitives — no fakes for
/// <see cref="IHttpContextAccessor"/> or <see cref="ClaimsPrincipal"/>, because the only logic here
/// is reading the accessor's current context, and a fake accessor would test the fake instead.
/// </summary>
public sealed class ClaimsPrincipalCallerContextTests
{
    [Fact]
    public void GetRoles_WhenNoHttpContext_ReturnsEmpty()
    {
        // The documented background-job case: no request in flight, accessor.HttpContext is null.
        var accessor = new HttpContextAccessor { HttpContext = null };
        var sut = new ClaimsPrincipalCallerContext(accessor);

        Assert.Empty(sut.GetRoles());
    }

    [Fact]
    public void GetRoles_WhenUserHasNoRoleClaims_ReturnsEmpty()
    {
        // DefaultHttpContext.User defaults to an unauthenticated ClaimsPrincipal, not null — the
        // anonymous-request case, distinct from no-HttpContext-at-all above.
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var sut = new ClaimsPrincipalCallerContext(accessor);

        Assert.Empty(sut.GetRoles());
    }

    [Fact]
    public void GetRoles_WhenUserHasRoleClaims_ReturnsThem()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "hr"),
            new Claim(ClaimTypes.Role, "finance"),
        ]);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var sut = new ClaimsPrincipalCallerContext(accessor);

        Assert.Equal(["hr", "finance"], sut.GetRoles());
    }

    [Fact]
    public void GetRoles_IgnoresNonRoleClaims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "alice"),
            new Claim(ClaimTypes.Role, "hr"),
            new Claim(ClaimTypes.Email, "alice@example.com"),
        ]);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var sut = new ClaimsPrincipalCallerContext(accessor);

        Assert.Equal(["hr"], sut.GetRoles());
    }

    [Fact]
    public void GetRoles_ReadsAcrossAllIdentitiesOnThePrincipal()
    {
        // ClaimsPrincipal.FindAll searches every ClaimsIdentity, not just the first — worth pinning
        // because a multi-scheme principal (e.g. cookie + a claims-transformation identity) is a
        // realistic ASP.NET Core shape, and role claims can land on either.
        var first = new ClaimsIdentity([new Claim(ClaimTypes.Role, "hr")]);
        var second = new ClaimsIdentity([new Claim(ClaimTypes.Role, "finance")]);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal([first, second]) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var sut = new ClaimsPrincipalCallerContext(accessor);

        Assert.Equal(["hr", "finance"], sut.GetRoles());
    }
}
