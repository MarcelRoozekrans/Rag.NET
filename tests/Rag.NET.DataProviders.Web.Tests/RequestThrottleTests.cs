using System.Diagnostics;
using Xunit;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary><see cref="RequestThrottle.RaiseTo"/>, which a site's <c>Crawl-delay</c> drives (#927).</summary>
public sealed class RequestThrottleTests
{
    private static async Task<TimeSpan> TwoStartsAsync(RequestThrottle throttle)
    {
        var stopwatch = Stopwatch.StartNew();
        await throttle.WaitAsync(TestContext.Current.CancellationToken);
        await throttle.WaitAsync(TestContext.Current.CancellationToken);
        return stopwatch.Elapsed;
    }

    [Fact]
    public async Task RaiseTo_NeverLowers()
    {
        var throttle = new RequestThrottle(TimeSpan.FromMilliseconds(200));
        throttle.RaiseTo(TimeSpan.FromMilliseconds(50));

        var elapsed = await TwoStartsAsync(throttle);

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(185), $"Took {elapsed.TotalMilliseconds} ms.");
    }

    [Fact]
    public async Task RaiseTo_RaisesAZeroInterval()
    {
        var throttle = new RequestThrottle(TimeSpan.Zero);
        throttle.RaiseTo(TimeSpan.FromMilliseconds(150));

        var elapsed = await TwoStartsAsync(throttle);

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(135), $"Took {elapsed.TotalMilliseconds} ms.");
    }

    /// <remarks>A start made while the interval was zero still counts: the request after a raise
    /// is spaced from it, not sent at once.</remarks>
    [Fact]
    public async Task AStartAtZeroInterval_CountsAfterARaise()
    {
        var throttle = new RequestThrottle(TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        await throttle.WaitAsync(TestContext.Current.CancellationToken);
        throttle.RaiseTo(TimeSpan.FromMilliseconds(150));
        await throttle.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(135), $"Took {stopwatch.Elapsed.TotalMilliseconds} ms.");
    }
}
