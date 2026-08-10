using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Rag.NET.DataProviders.Zendesk.Tests;

/// <summary>
/// Both Zendesk registrations copied their raw parameters into the options object and then built
/// the Basic-auth credentials from the parameters, so <c>ZendeskTicketsOptions.Email</c> and
/// <c>ZendeskArticlesOptions.Email</c> were never read and the configure callback could not
/// affect authentication (issue #108). These tests pin the options object as the single source
/// of truth by inspecting the shared named HttpClient the registration produces.
/// </summary>
/// <remarks>
/// Each test uses its own <see cref="ServiceCollection"/>: the two registrations share one
/// <c>IZendeskApi</c> client behind a marker, so only the first registration in a container
/// configures the HttpClient.
/// </remarks>
public sealed class ZendeskRegistrationTests
{
    private const string ApiClientName = "IZendeskApi";

    [Fact]
    public void Tickets_ConfigureCallbackChangingEmail_ChangesTheCredentialsSent()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "mycompany",
            email: "param@example.com",
            apiToken: "test-token",
            configure: o => o.Email = "override@example.com");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClientName);

        var auth = client.DefaultRequestHeaders.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("override@example.com/token:test-token")),
            auth.Parameter);
    }

    [Fact]
    public void Articles_ConfigureCallbackChangingEmail_ChangesTheCredentialsSent()
    {
        var services = new ServiceCollection();
        services.AddZendeskArticlesDataProvider(
            subdomain: "mycompany",
            email: "param@example.com",
            apiToken: "test-token",
            configure: o => o.Email = "override@example.com");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClientName);

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("override@example.com/token:test-token")),
            client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Tickets_NoConfigureCallback_ParameterEmailReachesTheCredentials()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "mycompany",
            email: "param@example.com",
            apiToken: "test-token");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClientName);

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("param@example.com/token:test-token")),
            client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Tickets_ConfigureCallbackBlankingEmail_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddZendeskTicketsDataProvider(
                subdomain: "mycompany",
                email: "param@example.com",
                apiToken: "test-token",
                configure: o => o.Email = " "));
    }

    [Fact]
    public void Articles_ConfigureCallbackBlankingEmail_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddZendeskArticlesDataProvider(
                subdomain: "mycompany",
                email: "param@example.com",
                apiToken: "test-token",
                configure: o => o.Email = " "));
    }
}
