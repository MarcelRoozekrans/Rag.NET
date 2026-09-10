using System.Diagnostics;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// Waits for Azure AI Search to make a write visible, by polling rather than by sleeping a fixed
/// guess.
/// </summary>
/// <remarks>
/// <para>
/// Every write in <c>AzureAISearchVectorStoreTests</c> used to be followed by
/// <c>await Task.Delay(2s)</c>. That is the same mistake the store itself made — its
/// <c>StoreAsync</c> slept a second on every call — and a fixed sleep is wrong in both directions:
/// too short and the test flakes, too long and every test pays the worst case whether or not it
/// needs to. A poll returns as soon as the service is actually ready, and on expiry fails naming
/// the condition that never came true rather than leaving an assertion to fail for a reason that
/// reads like a product bug.
/// </para>
/// <para>
/// <b>Lifted out of that class 2026-09-10</b>, when 6.2.34's pre-push review found a new test
/// reintroducing the fixed delay the class had already removed. It was <c>private static</c>
/// there, so the only thing available to a second test class was the mistake. Shared here so the
/// correct shape is the reachable one.
/// </para>
/// </remarks>
internal static class SearchIndexSettle
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Polls until <paramref name="what"/> holds, instead of sleeping a fixed guess.</summary>
    internal static async Task WaitUntilAsync(
        string what, Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= SettleTimeout)
            {
                Assert.Fail($"'{what}' never became true within {SettleTimeout.TotalSeconds:0}s.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
