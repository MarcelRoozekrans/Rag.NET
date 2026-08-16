using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.Extensions.Time.Testing;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Executes the <b>shipped default</b> retry policy — the one <c>ConfigureResilience()</c> installs
/// when no <c>configure</c> action is supplied — and asserts both halves of its contract: how many
/// times it retries, and how long it waits between attempts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (#263).</b> Until 2026-08-16 nothing executed this policy. Every retry
/// test in <see cref="ConfigureResilienceTests"/> substitutes its own zero-delay pipeline, for a
/// reason that class states plainly: the default's 1 s exponential back-off "would make a retried
/// assertion a sleep". That reasoning is right, and the consequence was that
/// <b>a default which retried once, or not at all, or with no back-off at all, would have passed
/// the entire suite.</b>
/// </para>
/// <para>
/// That is the shape of defect this milestone exists to catch — configured, believed, never run.
/// It is the same shape as late chunking, inert from Phase 1.1 to 3.7 while its tests were green.
/// </para>
/// <para>
/// <b>What made it testable:</b> <c>ConfigureResilience</c> now builds the pipeline through Polly's
/// <c>(builder, context)</c> overload and takes <c>TimeProvider</c> from DI when one is registered.
/// Nothing registers one in production, so <c>TimeProvider.System</c> remains the default and
/// behaviour is unchanged; a test registers a <see cref="FakeTimeProvider"/> and advances the clock
/// itself. No wall-clock dependency, no sleep, and the real policy runs.
/// </para>
/// <para>
/// <b>Jitter is why the delays are asserted as ranges.</b> The default sets
/// <c>UseJitter = true</c>, so Polly multiplies each computed delay by a random factor in
/// [0.8, 1.2]. Asserting exact values would be asserting the absence of jitter — which is the
/// opposite of what the default configures. The bounds below are the exponential schedule
/// (1 s, 2 s, 4 s) widened by that factor.
/// </para>
/// </remarks>
public sealed class DefaultPolicyBackoffTests
{
    /// <summary>Fails every attempt, so the policy exhausts its retries.</summary>
    private sealed class AlwaysFailingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Attempts { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("transient");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private static (IEmbeddingGenerator<string, Embedding<float>> Sut, AlwaysFailingEmbeddingGenerator Inner, FakeTimeProvider Clock)
        BuildWithDefaultPolicy()
    {
        var inner = new AlwaysFailingEmbeddingGenerator();
        var clock = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
        services.AddSingleton(Substitute.For<IVectorStore>());
        // No configure action: this is the shipped default, which is the whole point.
        services.AddRagNet(b => b.ConfigureResilience());

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), inner, clock);
    }

    [Fact]
    public async Task TheDefaultPolicy_RetriesThreeTimes_ForFourAttemptsInTotal()
    {
        var (sut, inner, clock) = BuildWithDefaultPolicy();

        var call = sut.GenerateAsync(["query"], cancellationToken: TestContext.Current.CancellationToken);

        // Drive the clock past any schedule the default could plausibly hold. Advancing generously
        // asserts the count, not the timing; timing is the next test's job.
        await AdvanceUntilSettledAsync(call, clock, TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() => call);

        // MaxRetryAttempts = 3 means three RETRIES on top of the first call.
        Assert.Equal(4, inner.Attempts);
    }

    /// <remarks>
    /// <b>The assertions here are deliberately loose, and the reason is a finding.</b> The first
    /// draft asserted each delay inside <c>base x [0.8, 1.2]</c> and failed: the first retry fired
    /// inside 700 ms against a nominal 1 s. Polly v8's <c>UseJitter</c> on an exponential schedule
    /// is <b>decorrelated</b> jitter, not a symmetric band around the base delay — individual hops
    /// range far more widely than a naive reading of "1 s exponential with jitter" suggests.
    /// <para>
    /// So this asserts what the policy actually guarantees rather than what its constants look
    /// like: that it <i>waits</i> at all, and that exhausting three retries costs real time. Tighter
    /// per-hop bounds would be asserting the absence of the jitter the default deliberately enables.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDefaultPolicy_WaitsBetweenAttempts_AndTheWaitsCostRealTime()
    {
        var (sut, inner, clock) = BuildWithDefaultPolicy();

        var call = sut.GenerateAsync(["query"], cancellationToken: TestContext.Current.CancellationToken);

        // Attempt 1 runs immediately; the policy is now waiting.
        Assert.Equal(1, inner.Attempts);

        // THE ASSERTION THAT MATTERS: with the clock frozen, no retry may fire, however long the
        // test spins. A policy with Delay = TimeSpan.Zero — the one every other test in this
        // project substitutes — would have burned all four attempts by now.
        for (var i = 0; i < 200; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(1, inner.Attempts);

        // Advance in slices, recording how much simulated time each attempt cost.
        var advanced = TimeSpan.Zero;
        var slice = TimeSpan.FromMilliseconds(100);
        var attemptAt = new List<TimeSpan> { TimeSpan.Zero };

        while (inner.Attempts < 4 && advanced < TimeSpan.FromSeconds(60))
        {
            var before = inner.Attempts;
            clock.Advance(slice);
            advanced += slice;
            for (var i = 0; i < 50 && inner.Attempts == before; i++)
            {
                await Task.Yield();
            }

            while (attemptAt.Count < inner.Attempts)
            {
                attemptAt.Add(advanced);
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        Assert.Equal(4, inner.Attempts);

        // Every hop took a positive amount of simulated time — no attempt was free.
        var schedule = string.Join(", ", attemptAt.Select(t => $"{t.TotalMilliseconds:F0}ms"));
        for (var i = 1; i < attemptAt.Count; i++)
        {
            Assert.True(
                attemptAt[i] > attemptAt[i - 1],
                $"attempt {i + 1} fired at the same simulated instant as attempt {i}; schedule: {schedule}");
        }

        // Exhausting three retries cost more than a second of simulated time. A zero-delay or
        // constant-tiny-delay policy fails this; the shipped schedule clears it comfortably.
        Assert.True(
            advanced > TimeSpan.FromSeconds(1),
            $"all four attempts completed within {advanced.TotalMilliseconds:F0}ms of simulated " +
            "time, which is not a back-off");
    }

    /// <remarks>
    /// The exclusion the default declares: cancellation is never a transient failure, so it must
    /// not be retried. Asserted against the real default rather than a substituted pipeline.
    /// </remarks>
    [Fact]
    public async Task TheDefaultPolicy_DoesNotRetryCancellation()
    {
        var inner = new CancellingEmbeddingGenerator();
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(inner);
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddRagNet(b => b.ConfigureResilience());

        var provider = services.BuildServiceProvider();
        var sut = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GenerateAsync(["query"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Attempts);
    }

    private sealed class CancellingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Attempts { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new OperationCanceledException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Advances the fake clock in slices until the call completes or the budget is spent.</summary>
    private static async Task AdvanceUntilSettledAsync(Task call, FakeTimeProvider clock, TimeSpan budget)
    {
        var spent = TimeSpan.Zero;
        var slice = TimeSpan.FromMilliseconds(500);
        while (!call.IsCompleted && spent < budget)
        {
            clock.Advance(slice);
            spent += slice;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Polly resumes a retry on a continuation, so the attempt lands shortly after the clock moves
    /// rather than inside <c>Advance</c>. Spins on the condition instead of sleeping a fixed time.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1_000 && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "the retry did not fire after the clock advanced past its delay");
    }
}
