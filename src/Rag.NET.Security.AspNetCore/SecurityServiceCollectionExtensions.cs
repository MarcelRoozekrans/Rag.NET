using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Security;

namespace Rag.NET.Security.AspNetCore;

/// <summary>Extension methods for registering ASP.NET Core security bindings.</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ClaimsPrincipalCallerContext"/> as the <see cref="ICallerContext"/>
    /// implementation. Call this in an ASP.NET Core project after <c>AddRagNet</c>.
    /// </summary>
    public static IServiceCollection AddRagNetAspNetCoreSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ICallerContext>(sp =>
            new ClaimsPrincipalCallerContext(sp.GetRequiredService<IHttpContextAccessor>()));
        return services;
    }
}
