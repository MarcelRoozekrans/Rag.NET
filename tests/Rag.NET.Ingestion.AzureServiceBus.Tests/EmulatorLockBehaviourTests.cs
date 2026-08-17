using Azure.Messaging.ServiceBus;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Establishes two facts about the emulator that #246 has twice been diagnosed against without
/// either being checked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> #246 is a <c>MessageLockLost</c> on a one-second test. It was first
/// attributed to lock expiry under load, and "fixed" by raising the queue's <c>LockDuration</c>
/// from <c>PT1M</c> to <c>PT5M</c>. It failed again with that change present, at 59.88 s into a run
/// on a test that took 1 s — which a 300-second lock cannot explain. The fix addressed a mechanism
/// nobody had shown was operating.
/// </para>
/// <para>
/// Rather than guess a third time, these two tests pin what the emulator actually does. They are
/// deliberately about the <i>emulator</i>, not about Rag.NET: the subject is the test fixture's own
/// foundations, and a fixture whose behaviour is assumed is how a flake survives two fixes.
/// </para>
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class EmulatorLockBehaviourTests(ServiceBusEmulatorFixture fixture)
{
    /// <summary>The configured lock is five minutes; this waits well past the old one-minute value.</summary>
    private static readonly TimeSpan PastTheOldLock = TimeSpan.FromSeconds(75);

    /// <remarks>
    /// <para>
    /// <b>Question 1: does the emulator honour <c>LockDuration</c> from <c>Config.json</c>?</b>
    /// </para>
    /// <para>
    /// Receive a message, hold it longer than the <i>old</i> PT1M setting without renewing, then
    /// settle it. Under the configured PT5M the lock is still valid and the settle succeeds. If the
    /// emulator ignores the setting and uses a shorter internal default, this throws
    /// <c>MessageLockLost</c> — and that would mean every "raise the lock duration" fix for #246 is
    /// inert, including the one already merged.
    /// </para>
    /// <para>
    /// Either outcome is worth having in writing. A pass says the configuration reaches the broker
    /// and #246 must be something else; a failure says the fixture has been configuring nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEmulatorHonoursTheConfiguredLockDuration()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = $"lockprobe-{Guid.NewGuid():N}";

        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage(body), ct);

        await using var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.QueueName);

        ServiceBusReceivedMessage? mine = null;
        var strays = new List<ServiceBusReceivedMessage>();
        try
        {
            while (mine is null)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct);
                Assert.NotNull(message);

                if (string.Equals(message.Body.ToString(), body, StringComparison.Ordinal))
                {
                    mine = message;
                }
                else
                {
                    strays.Add(message);
                }
            }

            // No renewal, deliberately: the point is what the broker does on its own.
            await Task.Delay(PastTheOldLock, ct);

            var lockLost = await Record.ExceptionAsync(() => receiver.CompleteMessageAsync(mine, ct));

            Assert.True(
                lockLost is null,
                $"The emulator did NOT honour the configured LockDuration: the lock was lost after " +
                $"{PastTheOldLock.TotalSeconds:F0}s despite Config.json declaring PT5M. Every fix " +
                $"for #246 that works by raising LockDuration is therefore inert. Exception: {lockLost}");
        }
        finally
        {
            foreach (var stray in strays)
            {
                await receiver.AbandonMessageAsync(stray, propertiesToModify: null, ct);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// <b>Question 2: can a processor still settle messages after <c>StopAsync</c> returns?</b>
    /// </para>
    /// <para>
    /// This is the other candidate for #246. Its failing call is
    /// <c>CompleteMessageAsync</c> on a dead-letter message the test had just received — so
    /// something settled that message in between. The tests are <c>[Collection]</c>-serialised, so
    /// it cannot be a concurrent sibling <i>method</i>; a processor from the <i>previous</i> test
    /// that outlives its <c>StopAsync</c> would fit exactly.
    /// </para>
    /// <para>
    /// A message sent strictly after <c>StopAsync</c> returns must still be on the queue afterwards.
    /// If it is not, a stopped processor is still consuming, and every test in this class is racing
    /// its predecessor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStoppedProcessorConsumesNothingMore()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var processor = client.CreateProcessor(
            ServiceBusEmulatorFixture.QueueName, new ServiceBusProcessorOptions());

        var consumed = 0;
        processor.ProcessMessageAsync += async args =>
        {
            _ = Interlocked.Increment(ref consumed);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        await processor.StartProcessingAsync(ct);
        await processor.StopProcessingAsync(ct);

        var afterStop = Interlocked.CompareExchange(ref consumed, 0, 0);

        var body = $"afterstop-{Guid.NewGuid():N}";
        await using var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage(body), ct);

        // Long enough that a still-running processor would have taken it.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(afterStop, Interlocked.CompareExchange(ref consumed, 0, 0));

        var found = await DrainForAsync(client, body, ct);

        Assert.True(
            found,
            "A message sent AFTER StopProcessingAsync returned was gone from the queue. A stopped " +
            "processor is still consuming, which makes every test in this class race its " +
            "predecessor over a shared queue — the mechanism #246 needs.");
    }

    /// <summary>Drains the queue looking for <paramref name="body"/>, abandoning everything else.</summary>
    private static async Task<bool> DrainForAsync(ServiceBusClient client, string body, CancellationToken ct)
    {
        await using var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.QueueName);
        var strays = new List<ServiceBusReceivedMessage>();
        try
        {
            while (true)
            {
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15), ct);
                if (message is null)
                {
                    return false;
                }

                if (string.Equals(message.Body.ToString(), body, StringComparison.Ordinal))
                {
                    await receiver.CompleteMessageAsync(message, ct);
                    return true;
                }

                strays.Add(message);
            }
        }
        finally
        {
            foreach (var stray in strays)
            {
                await receiver.AbandonMessageAsync(stray, propertiesToModify: null, ct);
            }
        }
    }
}
