using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Drives <see cref="EmbeddedTraversal"/> directly, over fake adapters and a descent policy the
/// test supplies. Reaching the driver below the parsers is what makes the depth claim testable
/// at all — see <c>docs/plans/2026-07-29-email-traversal-flattening-design.md</c>, §3.
/// </summary>
public class EmbeddedTraversalTests
{
    [Fact]
    public async Task ATenThousandLevelChainDoesNotConsumeStack()
    {
        // The claim this whole phase exists to make. The overflow floor is ~500 levels and the
        // ceiling is 64, so no test reaching through EmailParserOptions can construct a case that
        // would ever have overflowed — see the design, §3. Driving the traversal directly is the
        // only way to reach a depth that proves anything.
        //
        // IF THIS TEST TERMINATES THE TEST RUNNER RATHER THAN FAILING, THAT IS THE SIGNAL.
        // STATUS_STACK_OVERFLOW is uncatchable, so recursion reintroduced into the traversal kills
        // the process instead of reporting a failure. It is not flakiness. Read the traversal.
        //
        // Verified to fail that way: with the descent replaced by a recursive await foreach, this
        // test crashed the runner with exit code -1073741571 (0xC00000FD).
        var sections = await DriveChainAsync(depth: 10_000);

        Assert.Equal(10_001, sections.Count);
    }

    [Fact]
    public async Task ChildrenAreEmittedDepthFirstInDocumentOrder()
    {
        var adapter = new TreeAdapter();
        var sections = await DriveTreeAsync(BuildBranchingTree(), adapter, new AlwaysDescendPolicy());

        // A parent's child i expands completely — its own children included — before i+1 is
        // touched. Breadth-first would put "C" and "Child 2" ahead of "B" and "Grandchild".
        Assert.Equal(
            [
                "Root Subject", "root body", "A",
                "Child 1", "child 1 body", "B",
                "Grandchild", "grandchild body",
                "C",
                "Child 2", "child 2 body",
            ],
            sections.Select(s => s.Text).ToArray());

        // Draining to the end pops every frame, so nothing is left for the finally to clean up.
        Assert.Equal(4, adapter.EnumeratorsCreated);
        Assert.Equal(4, adapter.EnumeratorsDisposed);
    }

    [Fact]
    public async Task PolicyReturningNull_SkipsThatBranchAndContinuesWithTheNextSibling()
    {
        var adapter = new TreeAdapter();
        var sections = await DriveTreeAsync(BuildBranchingTree(), adapter, new DenyByNamePolicy("Child 1"));

        // "Child 1" and everything below it disappears; the siblings that follow it do not.
        Assert.Equal(
            ["Root Subject", "root body", "A", "C", "Child 2", "child 2 body"],
            sections.Select(s => s.Text).ToArray());

        // The denied branch never had a frame, so its children enumerator was never created.
        Assert.Equal(2, adapter.EnumeratorsCreated);
        Assert.Equal(2, adapter.EnumeratorsDisposed);
    }

    [Fact]
    public async Task AbandoningEnumerationEarly_DisposesEveryFrameLeftOnTheStack()
    {
        var adapter = new TreeAdapter();
        int seen = 0;

        await foreach (var _ in EmbeddedTraversal.RunAsync(
            BuildChain(depth: 5),
            adapter,
            CreateContext(),
            new AlwaysDescendPolicy(),
            [],
            logger: null,
            TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            if (++seen == 3)
                break;
        }

        // Breaking out of an await foreach calls the driver's DisposeAsync, which runs its
        // finally — so all three frames still on the stack release their enumerators.
        Assert.Equal(3, adapter.EnumeratorsCreated);
        Assert.Equal(3, adapter.EnumeratorsDisposed);
    }

    // ── Driving ──────────────────────────────────────────────────────────────

    private static async Task<List<DocumentSection>> DriveChainAsync(int depth)
    {
        var sections = new List<DocumentSection>();
        await foreach (var section in EmbeddedTraversal.RunAsync(
            new FakeMessage(0),
            new ChainAdapter(depth),
            CreateContext(),
            new AlwaysDescendPolicy(),
            [],
            logger: null,
            TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            sections.Add(section);
        }

        return sections;
    }

    private static async Task<List<DocumentSection>> DriveTreeAsync(
        FakeNode root,
        TreeAdapter adapter,
        IDescentPolicy policy)
    {
        var sections = new List<DocumentSection>();
        await foreach (var section in EmbeddedTraversal.RunAsync(
            root,
            adapter,
            CreateContext(),
            policy,
            [new FakeTextParser()],
            logger: null,
            TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            sections.Add(section);
        }

        return sections;
    }

    private static EmbeddedMessageContext CreateContext() =>
        EmbeddedMessageContext.Create(
            new DocumentMetadata
            {
                DocumentId = new DocumentId("traversal-1"),
                FileName = "root.eml",
                ContentType = "message/rfc822",
            },
            new EmailParserOptions());

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deliberately wide <i>and</i> deep: a flattening bug shows up as depth-first quietly
    /// becoming breadth-first, which only siblings following a descent can catch.
    /// </summary>
    private static FakeNode BuildBranchingTree()
    {
        var grandchild = new FakeNode { Subject = "Grandchild", Body = "grandchild body" };
        var child1 = new FakeNode { Subject = "Child 1", Body = "child 1 body" };
        child1.Children.Add(FileChild("B"));
        child1.Children.Add(EmbeddedChild("Grandchild", grandchild));

        var root = new FakeNode { Subject = "Root Subject", Body = "root body" };
        root.Children.Add(FileChild("A"));
        root.Children.Add(EmbeddedChild("Child 1", child1));
        root.Children.Add(FileChild("C"));
        root.Children.Add(EmbeddedChild("Child 2", new FakeNode { Subject = "Child 2", Body = "child 2 body" }));
        return root;
    }

    private static FakeNode BuildChain(int depth)
    {
        var node = new FakeNode { Subject = $"Level {depth}" };
        for (int level = depth - 1; level >= 0; level--)
        {
            var parent = new FakeNode { Subject = $"Level {level}" };
            parent.Children.Add(EmbeddedChild($"level-{level + 1}", node));
            node = parent;
        }

        return node;
    }

    private static MessageChild<FakeNode> FileChild(string text) => new()
    {
        Name = $"{text}.txt",
        MimeType = "text/plain",
        OpenAsync = _ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text))),
    };

    private static MessageChild<FakeNode> EmbeddedChild(string name, FakeNode node) => new()
    {
        Name = name,
        EmbeddedMessage = node,
    };

    /// <summary>One level of a synthetic chain; carries nothing but its own depth.</summary>
    private sealed record FakeMessage(int Depth);

    /// <summary>An explicit tree node, so a test can pin emission order against a known shape.</summary>
    private sealed class FakeNode
    {
        public required string Subject { get; init; }

        public string? Body { get; init; }

        public List<MessageChild<FakeNode>> Children { get; } = [];
    }

    /// <summary>
    /// Yields a single embedded child per level until <paramref name="maxDepth"/>, then nothing.
    /// Children are produced lazily by an iterator method, so a 10,000-level chain never
    /// materialises 10,000 messages at once.
    /// </summary>
    private sealed class ChainAdapter(int maxDepth) : IMessageAdapter<FakeMessage>
    {
        public string? GetSubject(FakeMessage message) => $"Level {message.Depth}";

        public IAsyncEnumerable<DocumentSection> ReadBodyAsync(
            FakeMessage message, DocumentMetadata metadata, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<DocumentSection>();

        public IEnumerator<MessageChild<FakeMessage>> ReadChildren(FakeMessage message) =>
            Descend(message).GetEnumerator();

        private IEnumerable<MessageChild<FakeMessage>> Descend(FakeMessage message)
        {
            if (message.Depth >= maxDepth)
                yield break;

            yield return new MessageChild<FakeMessage>
            {
                Name = $"level-{message.Depth + 1}",
                EmbeddedMessage = new FakeMessage(message.Depth + 1),
            };
        }
    }

    /// <summary>Walks a <see cref="FakeNode"/> tree, counting the enumerators it hands out.</summary>
    private sealed class TreeAdapter : IMessageAdapter<FakeNode>
    {
        public int EnumeratorsCreated { get; private set; }

        public int EnumeratorsDisposed { get; private set; }

        public string? GetSubject(FakeNode message) => message.Subject;

        public IAsyncEnumerable<DocumentSection> ReadBodyAsync(
            FakeNode message, DocumentMetadata metadata, CancellationToken cancellationToken) =>
            BodyAsync(message, metadata, cancellationToken);

        public IEnumerator<MessageChild<FakeNode>> ReadChildren(FakeNode message)
        {
            EnumeratorsCreated++;
            return new RecordingEnumerator(Enumerate(message), () => EnumeratorsDisposed++);
        }

        private static IEnumerator<MessageChild<FakeNode>> Enumerate(FakeNode message)
        {
            for (int index = 0; index < message.Children.Count; index++)
            {
                yield return message.Children[index];
            }
        }

        private static async IAsyncEnumerable<DocumentSection> BodyAsync(
            FakeNode message,
            DocumentMetadata metadata,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (message.Body is null)
                yield break;

            // A real body sub-parse (HtmlDocumentParser) is genuinely async; yielding the thread
            // keeps the driver's inline await foreach from being trivially synchronous here.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DocumentSection
            {
                Text = message.Body,
                DocumentId = metadata.DocumentId,
                SectionIndex = 0,
            };
        }
    }

    /// <summary>Reports its own disposal, so a test can prove the driver released every frame.</summary>
    private sealed class RecordingEnumerator(IEnumerator<MessageChild<FakeNode>> inner, Action onDispose)
        : IEnumerator<MessageChild<FakeNode>>
    {
        public MessageChild<FakeNode> Current => inner.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => inner.MoveNext();

        public void Reset() => inner.Reset();

        public void Dispose()
        {
            inner.Dispose();
            onDispose();
        }
    }

    /// <summary>
    /// Descends unconditionally, reusing the parent's metadata. Production wires depth and budget
    /// accounting in here; this is the seam that lets a test reach a depth the configured ceiling
    /// forbids.
    /// </summary>
    private sealed class AlwaysDescendPolicy : IDescentPolicy
    {
        public EmbeddedMessageContext? TryDescend(EmbeddedMessageContext parent, string name) =>
            parent.Descend(parent.Metadata);
    }

    /// <summary>Refuses one named branch, so a test can pin what "skip" costs its siblings.</summary>
    private sealed class DenyByNamePolicy(string denied) : IDescentPolicy
    {
        public EmbeddedMessageContext? TryDescend(EmbeddedMessageContext parent, string name) =>
            string.Equals(name, denied, StringComparison.Ordinal)
                ? null
                : parent.Descend(parent.Metadata);
    }
}
