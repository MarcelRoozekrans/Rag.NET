using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Security.AspNetCore.Tests;

/// <summary>
/// <see cref="SecurityServiceCollectionExtensions.AddRagNetAspNetCoreSecurity"/> driven through a
/// real ASP.NET Core request pipeline (<see cref="TestServer"/>), not a service provider built and
/// inspected in isolation. The registration's whole point is that a singleton
/// <see cref="ICallerContext"/> still answers correctly per request via
/// <see cref="IHttpContextAccessor"/>'s <c>AsyncLocal</c> flow — that claim is only proven by
/// sending requests, concurrently, and checking nothing leaks between them.
/// </summary>
public sealed class SecurityServiceCollectionExtensionsTests
{
    [Fact]
    public async Task Roles_EndToEnd_ReflectsTheRequestsClaims()
    {
        using var host = await StartAsync(services => services.AddRagNetAspNetCoreSecurity());
        using var client = host.GetTestClient();

        var response = await GetRolesAsync(client, "hr,finance");

        Assert.Equal("hr,finance", response);
    }

    [Fact]
    public async Task Roles_WhenRequestCarriesNoRoleClaims_IsEmpty()
    {
        using var host = await StartAsync(services => services.AddRagNetAspNetCoreSecurity());
        using var client = host.GetTestClient();

        var response = await GetRolesAsync(client, roles: null);

        Assert.Equal(string.Empty, response);
    }

    [Fact]
    public async Task Roles_UnderConcurrentRequests_NeverLeaksBetweenCallers()
    {
        // The registration is a singleton (see the type's remarks); this is the test that would
        // fail if that singleton read a shared/static field instead of the accessor's per-request,
        // AsyncLocal-backed context. Sequential requests would not catch that class of bug because
        // each would still just observe "whatever was set most recently" — concurrency is required.
        using var host = await StartAsync(services => services.AddRagNetAspNetCoreSecurity());
        using var client = host.GetTestClient();

        var callerRoles = Enumerable.Range(0, 16).Select(i => $"role-{i}").ToArray();
        var responses = await Task.WhenAll(
            callerRoles.Select(role => GetRolesAsync(client, role)));

        for (var i = 0; i < callerRoles.Length; i++)
        {
            Assert.Equal(callerRoles[i], responses[i]);
        }
    }

    [Fact]
    public async Task AddRagNetAspNetCoreSecurity_DoesNotOverrideAnAlreadyRegisteredCallerContext()
    {
        // TryAddSingleton, not AddSingleton: a host that already supplied its own ICallerContext
        // (a custom claims scheme, a non-HTTP host reusing the same DI container, ...) keeps it.
        using var host = await StartAsync(services =>
        {
            services.AddSingleton<ICallerContext>(new FixedCallerContext("custom-role"));
            services.AddRagNetAspNetCoreSecurity();
        });
        using var client = host.GetTestClient();

        // Even though the request itself carries a different role, the pre-registered
        // ICallerContext must win — AddRagNetAspNetCoreSecurity is not allowed to replace it.
        var response = await GetRolesAsync(client, "hr");

        Assert.Equal("custom-role", response);
    }

    [Fact]
    public async Task Rbac_ViaTheRealPipeline_PassesAChunkTheCallersRoleAllows()
    {
        using var host = await StartAsync(services =>
        {
            new RagBuilder(services).UseRbac();
            services.AddRagNetAspNetCoreSecurity();
        });
        using var client = host.GetTestClient();

        var visible = await GetRbacVisibleDocumentsAsync(client, "hr");

        Assert.Equal(["public-doc", "hr-doc"], visible);
    }

    [Fact]
    public async Task Rbac_ViaTheRealPipeline_FiltersAChunkTheCallersRoleDoesNotAllow()
    {
        // The negative path: this is the test that would fail silently if the ClaimsPrincipal ↔
        // ICallerContext bridge stopped working — the caller would simply see everything, which is
        // exactly the failure mode security-adjacent code must not have.
        using var host = await StartAsync(services =>
        {
            new RagBuilder(services).UseRbac();
            services.AddRagNetAspNetCoreSecurity();
        });
        using var client = host.GetTestClient();

        var visible = await GetRbacVisibleDocumentsAsync(client, "finance");

        Assert.Equal(["public-doc"], visible);
    }

    [Fact]
    public async Task Rbac_ViaTheRealPipeline_UnauthenticatedRequestOnlySeesPublicChunks()
    {
        using var host = await StartAsync(services =>
        {
            new RagBuilder(services).UseRbac();
            services.AddRagNetAspNetCoreSecurity();
        });
        using var client = host.GetTestClient();

        var visible = await GetRbacVisibleDocumentsAsync(client, roles: null);

        Assert.Equal(["public-doc"], visible);
    }

    private static async Task<string?> GetRolesAsync(HttpClient client, string? roles)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/roles");
        if (roles is not null)
            request.Headers.Add("X-Roles", roles);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> GetRbacVisibleDocumentsAsync(HttpClient client, string? roles)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/rbac");
        if (roles is not null)
            request.Headers.Add("X-Roles", roles);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<string[]>(TestContext.Current.CancellationToken);
        return body ?? [];
    }

    /// <summary>Starts a test host with a claims-setting middleware and the fixed RBAC dataset mapped.</summary>
    /// <param name="configureServices">Registers the security services under test.</param>
    /// <returns>The running host.</returns>
    private static async Task<IHost> StartAsync(Action<IServiceCollection> configureServices) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    configureServices(services);
                })
                .Configure(app =>
                {
                    // Stands in for real authentication middleware: an "X-Roles" request header
                    // becomes the request's ClaimsPrincipal role claims, so ClaimsPrincipalCallerContext
                    // has something real to read from IHttpContextAccessor.
                    app.Use(async (ctx, next) =>
                    {
                        var header = ctx.Request.Headers["X-Roles"].ToString();
                        if (!string.IsNullOrEmpty(header))
                        {
                            var claims = header
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(role => new Claim(ClaimTypes.Role, role));
                            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
                        }

                        await next(ctx);
                    });

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/roles", ([FromServices] ICallerContext caller) =>
                            string.Join(",", caller.GetRoles()));

                        // [FromServices], not inferred: when a test's host does not call UseRbac(),
                        // IRetrievalGuard is unregistered, and minimal APIs validate every mapped
                        // endpoint's parameter sources against the container at first request — an
                        // unregistered, un-annotated reference type would be inferred as a request
                        // body instead, which GET does not allow, and mapping would throw for every
                        // test in this class rather than just the ones that hit "/rbac".
                        endpoints.MapGet("/rbac", ([FromServices] IRetrievalGuard guard) =>
                        {
                            var visible = guard.Inspect(RbacDataset());
                            return visible.Select(result => result.Chunk.DocumentId.Value).ToArray();
                        });
                    });
                }))
            .StartAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    /// <summary>One public chunk (no <c>allowed_roles</c>) and one restricted to <c>hr</c>.</summary>
    private static IReadOnlyList<SearchResult> RbacDataset() =>
    [
        MakeResult("public-doc", allowedRoles: null),
        MakeResult("hr-doc", allowedRoles: "hr"),
    ];

    private static SearchResult MakeResult(string documentId, string? allowedRoles)
    {
        var chunk = new TextChunk
        {
            Text = "chunk text",
            DocumentId = new DocumentId(documentId),
            ChunkIndex = 0,
        };
        if (allowedRoles is not null)
            chunk.Metadata["allowed_roles"] = allowedRoles;

        return new SearchResult { Score = 1.0, Chunk = chunk };
    }

    /// <summary>Fixed <see cref="ICallerContext"/> test double, for proving <c>TryAddSingleton</c> yields.</summary>
    private sealed class FixedCallerContext(params string[] roles) : ICallerContext
    {
        public IReadOnlyList<string> GetRoles() => roles;
    }
}
