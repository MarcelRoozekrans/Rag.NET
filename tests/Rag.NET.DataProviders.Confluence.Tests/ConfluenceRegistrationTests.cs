using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Rag.NET.DataProviders.Confluence.Tests;

/// <summary>
/// <c>AddConfluenceDataProvider</c> copied its raw parameters into <see cref="ConfluenceOptions"/>
/// and then configured the HttpClient from the parameters, so the options object's required
/// <c>BaseUrl</c>/<c>Email</c> were never read and the configure callback could not affect the
/// client (issue #108). These tests pin the options object as the single source of truth by
/// inspecting the named HttpClient the registration produces.
/// </summary>
public sealed class ConfluenceRegistrationTests
{
    private const string ApiClientName = "IConfluenceApi";

    [Fact]
    public void ConfigureCallback_ChangingBaseUrlAndEmail_ReachesTheHttpClient()
    {
        var services = new ServiceCollection();
        services.AddConfluenceDataProvider(
            "https://param.atlassian.net/wiki",
            "param@example.com",
            "api-token",
            o =>
            {
                o.BaseUrl = "https://override.atlassian.net/wiki";
                o.Email = "override@example.com";
            });

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClientName);

        Assert.Equal(new Uri("https://override.atlassian.net/wiki"), client.BaseAddress);
        var auth = client.DefaultRequestHeaders.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("override@example.com:api-token")),
            auth.Parameter);
    }

    [Fact]
    public void NoConfigureCallback_ParametersReachTheHttpClient()
    {
        var services = new ServiceCollection();
        services.AddConfluenceDataProvider(
            "https://param.atlassian.net/wiki", "param@example.com", "api-token");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiClientName);

        Assert.Equal(new Uri("https://param.atlassian.net/wiki"), client.BaseAddress);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("param@example.com:api-token")),
            client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void ConfigureCallback_BlankingEmail_ThrowsInsteadOfSendingBrokenCredentials()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddConfluenceDataProvider(
                "https://param.atlassian.net/wiki", "param@example.com", "api-token",
                o => o.Email = " "));
    }
}
