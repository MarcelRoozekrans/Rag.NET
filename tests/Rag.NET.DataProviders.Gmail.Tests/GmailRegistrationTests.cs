using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.Gmail.Tests;

/// <summary>
/// The registration extension's <c>configure</c> callback can actually configure the provider.
/// <para>
/// <b>It could not.</b> Every user-facing property on <see cref="GmailOptions"/> was
/// <c>init</c>-only while <c>AddGmailDataProvider</c> handed the callback an already-constructed
/// instance, so <c>opts.UserName = "…"</c> inside it did not compile (CS8852). The only settable
/// member was the inherited <c>DeltaToken</c>, which is delta bookkeeping rather than
/// configuration — meaning <c>UserName</c> stayed <see cref="string.Empty"/> through the one
/// public registration path the package offers, and IMAP OAuth2 authenticated as nobody.
/// </para>
/// <para>
/// A test asserting the provider resolves would have passed throughout: the registration succeeds,
/// a singleton appears, and nothing observes that the options never took the caller's values. So
/// this asserts the values, and it is a compile-time guard as much as a runtime one — if the
/// properties go back to <c>init</c>, this file stops building rather than starts failing.
/// </para>
/// </summary>
public sealed class GmailRegistrationTests
{
    [Fact]
    public void AddGmailDataProvider_ConfigureCallback_ActuallySetsTheOptions()
    {
        var services = new ServiceCollection();
        GmailOptions? captured = null;

        services.AddGmailDataProvider(new StaticTokenProvider("token"), opts =>
        {
            opts.UserName = "me@example.com";
            opts.Query = "label:support newer_than:30d";
            opts.MaxResults = 25;
            opts.Extensions = [".md"];
            captured = opts;
        });

        Assert.NotNull(captured);
        Assert.Equal("me@example.com", captured.UserName);
        Assert.Equal("label:support newer_than:30d", captured.Query);
        Assert.Equal(25, captured.MaxResults);
        Assert.Equal([".md"], captured.Extensions);
    }

    [Fact]
    public void AddGmailDataProvider_RegistersTheProviderAsAFileContentProvider()
    {
        var services = new ServiceCollection();

        services.AddGmailDataProvider(new StaticTokenProvider("token"), opts =>
            opts.UserName = "me@example.com");

        var provider = Assert.Single(
            services.BuildServiceProvider().GetServices<IFileContentProvider>());
        Assert.IsType<GmailDataProvider>(provider);
    }
}
