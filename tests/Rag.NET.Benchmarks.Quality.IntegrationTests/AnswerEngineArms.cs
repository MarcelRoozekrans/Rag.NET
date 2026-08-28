using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Builds the answer engine each engine arm generates with, over the harness's own answering
/// client.
/// <para>
/// Every engine receives the shared <c>CachedGraphRagClient</c> and builds its own prompts. The
/// answer cache is keyed on prompt text, so each engine's prompts are new keys and no existing
/// entry is touched — which is what keeps <c>dense</c>, <c>global</c> and the RAPTOR arms
/// reproducible while these arms are added.
/// </para>
/// </summary>
internal static class AnswerEngineArms
{
    /// <summary>
    /// Creates the engine for <paramref name="arm"/>.
    /// </summary>
    /// <param name="arm">One of the engine arms; anything else throws.</param>
    /// <param name="chatClient">The harness's answering client, shared by every arm.</param>
    /// <param name="retriever">
    /// The retriever the engine is built with. <see cref="AnswerArm.Flare"/> needs a live one — its
    /// lookahead retrieves mid-generation. <see cref="AnswerArm.FlareFixed"/> needs an
    /// <see cref="UnreachableRetriever"/>, and the caller must hold that instance, because the flag
    /// on it is the arm's guarantee and a stub this factory made and dropped would be unreadable —
    /// see that type's remarks for why the instance, not the throw, is what proves anything. Both
    /// are required: <see langword="null"/> for either arm throws. The three non-FLARE arms ignore
    /// it and are passed <see langword="null"/>.
    /// </param>
    /// <param name="failures">
    /// Where the engines' swallowed failures are counted — see <see cref="FailureLog"/>. Required,
    /// and deliberately not defaulted: a caller that forgets it would be back to discarding those
    /// logs, which is the state this parameter exists to end.
    /// </param>
    public static IAnswerEngine Create(
        string arm, IChatClient chatClient, IRetriever? retriever, FailureLog failures)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(failures);

        if (string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal))
        {
            return new ChatAnswerEngine(chatClient);
        }

        if (string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal))
        {
            return new MapReduceAnswerEngine(chatClient, failures.LoggerFor<MapReduceAnswerEngine>());
        }

        if (string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal))
        {
            return new RefineAnswerEngine(chatClient, failures.LoggerFor<RefineAnswerEngine>());
        }

        if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(retriever);
            return BuildFlare(chatClient, retriever, new FlareOptions { MaxRetrievals = 0 }, failures);
        }

        if (string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(retriever);
            return BuildFlare(chatClient, retriever, new FlareOptions(), failures);
        }

        throw new ArgumentOutOfRangeException(
            nameof(arm), arm, "Not an arm this factory builds an engine for.");
    }

    /// <summary>
    /// The two FLARE arms, which differ only in <see cref="FlareOptions.MaxRetrievals"/>.
    /// </summary>
    /// <remarks>
    /// The scorer gets a counting logger too, and that is not incidental.
    /// <c>SelfAssessmentConfidenceScorer</c> fails open — an erroring or unparsable self-assessment
    /// scores <c>1.0</c>, above the threshold, so no lookahead fires and <c>flare</c> quietly
    /// answers the way <c>flarefixed</c> would. That is the same silent degradation the map and
    /// refine catches produce, and it is counted the same way.
    /// </remarks>
    private static FlareAnswerEngine BuildFlare(
        IChatClient chatClient, IRetriever retriever, FlareOptions options, FailureLog failures) =>
        new(
            chatClient,
            retriever,
            new SelfAssessmentConfidenceScorer(
                chatClient, failures.LoggerFor<SelfAssessmentConfidenceScorer>()),
            options,
            failures.LoggerFor<FlareAnswerEngine>());

    /// <summary>Reports whether <paramref name="arm"/> generates through an <see cref="IAnswerEngine"/>.</summary>
    public static bool IsEngineArm(string arm) =>
        string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal);

    /// <summary>
    /// An <see cref="IRetriever"/> that records whether it was called, then throws.
    /// </summary>
    /// <remarks>
    /// <see cref="AnswerArm.FlareFixed"/>'s whole claim is that lookahead is off. The throw alone is
    /// <b>not</b> a structural guarantee of that: <c>FlareAnswerEngine.TryLookaheadRetrievalAsync</c>
    /// wraps the retriever call in a catch-all that logs and swallows every exception, including this
    /// one, and returns as if the lookahead simply found nothing — the engine keeps running, still
    /// makes its call count, and a test watching only "did it throw" or "did calls happen" would pass
    /// while lookahead had actually fired. <see cref="WasCalled"/> is set <b>before</b> the throw, so
    /// it survives that swallowing and is the guarantee callers should assert on. The throw is kept
    /// anyway: it is still correct behaviour for any caller that does not swallow it, and it costs
    /// nothing to leave in.
    /// </remarks>
    internal sealed class UnreachableRetriever : IRetriever
    {
        /// <summary>
        /// <see langword="true"/> once <see cref="RetrieveAsync"/> has been entered, regardless of
        /// what the caller does with the exception it then throws.
        /// </summary>
        public bool WasCalled { get; private set; }

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException(
                "flarefixed retrieved mid-generation. MaxRetrievals is 0, so this is unreachable " +
                "unless FLARE's lookahead guard changed — the arm is no longer holding retrieval " +
                "fixed and its comparison against mapreduce/refine is invalid.");
        }
    }

    /// <summary>
    /// The <see cref="ILogger"/> the engines are built with: it counts the failures they swallow,
    /// so a run can assert that none happened instead of discarding them into a null logger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Three of the engines answer through a catch-all.
    /// <c>MapReduceAnswerEngine.MapOneAsync</c> catches every non-cancellation exception, logs it
    /// and returns <see langword="null"/> — the reduce step then synthesises whatever partials
    /// survived. <c>RefineAnswerEngine</c> catches per source and keeps the previous answer.
    /// <c>SelfAssessmentConfidenceScorer</c> catches and fails open at <c>1.0</c>, above the
    /// threshold, so no lookahead fires and <c>flare</c> answers the way <c>flarefixed</c> would.
    /// All three <b>answer from less than they were given</b> and report nothing to their caller;
    /// the only trace is the log. With <c>NullLogger</c>, which is what this factory used to pass,
    /// that trace was discarded.
    /// </para>
    /// <para>
    /// <b>What that costs on a replay run.</b> A missing answer-cache entry throws out of
    /// <c>GraphExtractionCache.MissingEntry</c>. Inside a map call that throw is swallowed, so the
    /// arm answers from five chunks instead of six and produces a <i>lower accuracy figure that
    /// looks exactly like a result</i>. The call-shape gate does not catch it: its counter
    /// increments before the request is forwarded, so a call that threw is still a call it counted.
    /// <see cref="AssertNoExceptionWasSwallowed"/> is the gate that does, which is what makes "a
    /// replay cannot silently differ" true rather than nearly true.
    /// </para>
    /// <para>
    /// <b>Only exceptions fail the run, and that distinction is deliberate.</b> Every swallowed
    /// <i>exception</i> is a fault: something threw and the engine carried on. But two of the
    /// warnings carry no exception — <c>ConfidenceScoreUnparsable</c>, when the model's
    /// self-assessment does not parse as a number, and <c>FlareLookaheadFailed</c>, when retrieval
    /// returns an error result. Those are the model's or the store's output, not a fault, and
    /// failing on them would be self-perpetuating: the unparsable reply is itself cached, so every
    /// replay would reproduce it and <c>flare</c> could never run again without a code change. They
    /// are counted and printed loudly in the cost block, where <see cref="Count"/> exceeding
    /// <see cref="SwallowedExceptions"/> tells a reader the scorer is failing open, and Gate 3
    /// backs that up by asserting the lookahead fired at all.
    /// </para>
    /// <para>
    /// Counting rather than printing is deliberate: text in a test log is only a guarantee if
    /// somebody reads it, and this one has to hold on an unattended run.
    /// </para>
    /// </remarks>
    internal sealed class FailureLog
    {
        /// <summary>How many reports are kept for the message; the counts are exact regardless.</summary>
        private const int MaxKeptReports = 10;

        private readonly ConcurrentQueue<string> _reports = new();
        private readonly ConcurrentDictionary<string, int> _byEngine = new(StringComparer.Ordinal);
        private int _count;
        private int _exceptions;

        /// <summary>Every warning and error logged by the engines built against this instance.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Those of them that carried an exception — the ones that fail the run.</summary>
        public int SwallowedExceptions => Volatile.Read(ref _exceptions);

        /// <summary>The logger for <typeparamref name="T"/>, counting into this instance.</summary>
        public ILogger<T> LoggerFor<T>() => new Sink<T>(this);

        /// <summary>A per-engine breakdown and the first few messages, for the run's cost block.</summary>
        public string Describe()
        {
            var builder = new StringBuilder();
            _ = builder.Append(FormattableString.Invariant(
                $"engine failures swallowed: {SwallowedExceptions} with an exception (must be 0), {Count} warnings in total"));
            if (Count == 0)
            {
                return builder.ToString();
            }

            _ = builder.Append(" —");
            foreach (var pair in _byEngine)
            {
                _ = builder.Append(FormattableString.Invariant($" {pair.Key}={pair.Value}"));
            }

            foreach (var report in _reports)
            {
                _ = builder.AppendLine().Append("    ").Append(report);
            }

            return builder.ToString();
        }

        /// <summary>
        /// No engine caught an exception, logged it and answered anyway.
        /// </summary>
        /// <remarks>
        /// Read after the answering phase has completed, so every write happens-before this read.
        /// A non-zero count means at least one answer was generated from less than the arm was
        /// handed, and the run's accuracy figure is not the figure the arm would produce — so the
        /// run fails here rather than publishing it.
        /// </remarks>
        public void AssertNoExceptionWasSwallowed() =>
            Assert.True(
                SwallowedExceptions == 0,
                "ENGINE FAILURES SWALLOWED. An engine caught an exception, logged it and answered " +
                "anyway — from fewer chunks (mapreduce/refine), or with the confidence scorer " +
                "failed open, which turns flare into flarefixed for that sentence. Whatever " +
                "accuracy this run reports is not the arm's behaviour, and the call-shape gate " +
                "cannot see it: its counter increments before the request is forwarded, so a call " +
                "that threw was still counted. On a replay run this is most often a missing " +
                "answer-cache entry — fill the cache and re-run rather than reading the figure. " +
                Describe());

        private void Record(string engine, string message, Exception? exception)
        {
            _ = Interlocked.Increment(ref _count);
            if (exception is not null)
            {
                _ = Interlocked.Increment(ref _exceptions);
            }

            _ = _byEngine.AddOrUpdate(engine, 1, static (_, existing) => existing + 1);

            if (_reports.Count >= MaxKeptReports)
            {
                return;
            }

            _reports.Enqueue(exception is null
                ? engine + ": " + message
                : engine + ": " + message + " — " + exception.GetType().Name + ": " + exception.Message);
        }

        /// <summary>The <see cref="ILogger{TCategoryName}"/> one engine writes into.</summary>
        /// <remarks>
        /// <see cref="IsEnabled"/> must return <see langword="true"/> at
        /// <see cref="LogLevel.Warning"/> and above: <c>AnswerEngineLog</c> is source-generated by
        /// <c>[LoggerMessage]</c>, which checks <see cref="IsEnabled"/> first and does not call
        /// <see cref="Log"/> when it returns <see langword="false"/> — a logger that disabled
        /// itself would count nothing and read as a clean run.
        /// </remarks>
        private sealed class Sink<T> : ILogger<T>
        {
            private readonly FailureLog _owner;

            public Sink(FailureLog owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                if (logLevel < LogLevel.Warning)
                {
                    return;
                }

                _owner.Record(typeof(T).Name, formatter(state, exception), exception);
            }
        }
    }
}
