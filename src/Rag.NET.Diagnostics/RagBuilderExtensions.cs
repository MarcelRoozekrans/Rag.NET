using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Diagnostics.Internal;

namespace Rag.NET.Diagnostics;

/// <summary>Registration for the pipeline debugger.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Turns on trace capture: the last <see cref="RagTraceOptions.Capacity"/> query executions, in
    /// memory, readable through <see cref="ITraceStore"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type, so the call chains.</typeparam>
    /// <param name="builder">The RAG builder. Requires <c>AddRagNet</c> to have been called.</param>
    /// <param name="configure">
    /// Optional. Every <c>Capture*</c> flag is <see langword="false"/> without it, so this registers a
    /// debugger that records structure and no text at all — see <see cref="RagTraceOptions"/> for what
    /// each flag puts in process memory before turning one on.
    /// </param>
    /// <returns>The builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>Call this last.</b> It reads the service collection <i>at the time of the call</i> for the
    /// guards and sanitisers — the same ordering rule <c>ConfigureResilience</c> and
    /// <c>UseRateLimiting</c> carry. It decorates the <c>IRetrievalGuard</c>, <c>IChunkSanitiser</c>
    /// and <c>IQuerySanitiser</c> registrations that exist by then. A guard registered afterwards
    /// still runs; it is simply not traced, and its absence from a trace then means "not registered
    /// when diagnostics was" rather than "never fired", which is a misleading answer to the question
    /// traces exist to answer.
    /// </para>
    /// <para>
    /// <b>The answer is the exception, and no longer order-sensitive.</b> Answer capture goes through
    /// <c>RagAnswerEngineDecorations</c>, which is applied when <c>RagPipeline</c> composes its
    /// engine, so it wraps whatever the container ends up answering with: an <c>IAnswerEngine</c>
    /// registered before or after this call, or the <c>ChatAnswerEngine</c> that <c>AddRagNet</c>
    /// builds from an <c>IChatClient</c> registered after <c>AddRagNet</c> returned. That last shape —
    /// <c>AddRagNet(rag => rag.AddRagDiagnostics())</c> followed by
    /// <c>services.AddSingleton&lt;IChatClient&gt;(…)</c>, which is the order every walkthrough
    /// produces — used to give a trace with a query, chunks, stages and a prompt whose <c>Answer</c>
    /// was always <see langword="null"/> even with <see cref="RagTraceOptions.CaptureAnswerText"/> on,
    /// while asking returned an answer perfectly well (issue #195). A retrieval-only pipeline still
    /// gets no engine: there is nothing to observe, and inventing one would break a pipeline that
    /// worked without diagnostics. Note that resolving <c>IAnswerEngine</c> from the container yields
    /// the <i>registered</i> engine, undecorated; <c>ComposedAnswerEngine</c> is the traced one.
    /// </para>
    /// <para>
    /// Finding nothing to decorate is a <b>no-op, not a failure</b>. A pipeline with no guards and no
    /// sanitisers is a perfectly ordinary pipeline, and refusing to trace it — which is what copying
    /// <c>ConfigureResilience</c>'s "nothing to apply to" exception would have done — would make
    /// diagnostics depend on the security package being installed.
    /// </para>
    /// <para>
    /// Everything registered here is a singleton, including the <c>ActivityListener</c> that supplies
    /// stage timings and commits finished traces. That listener changes sampling for the
    /// <c>Rag.NET</c> <c>ActivitySource</c>: its spans start being created even with no exporter
    /// configured, which is unavoidable — an unsampled <c>StartActivity</c> returns
    /// <see langword="null"/> and there is nothing to time.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><c>AddRagNet</c> has not been called.</exception>
    public static TBuilder AddRagDiagnostics<TBuilder>(
        this TBuilder builder,
        Action<RagTraceOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RagTraceOptions();
        configure?.Invoke(options);

        RegisterCore(builder.Services, options);
        RegisterCaptureSeams(builder.Services);
        DecorateWhatIsRegistered(builder.Services);

        return builder;
    }

    /// <summary>Registers the options, the buffer, the collector and the span listener.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The configured options, already validated by their own setters.</param>
    private static void RegisterCore(IServiceCollection services, RagTraceOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(new TraceRingBuffer(options.Capacity));
        services.AddSingleton<ITraceStore>(static sp => sp.GetRequiredService<TraceRingBuffer>());

        services.AddSingleton<TraceCollector>(static sp => new TraceCollector(
            sp.GetRequiredService<RagTraceOptions>(),
            sp.GetRequiredService<TraceRingBuffer>(),
            sp.GetService<ILogger<TraceCollector>>()));

        services.AddSingleton<StageActivityListener>(static sp => new StageActivityListener(
            sp.GetRequiredService<TraceCollector>(),
            sp.GetService<ILogger<StageActivityListener>>()));

        // The listener subscribes in its constructor, so a container-owned singleton nobody resolves
        // never subscribes and the whole feature silently does nothing. Resolving ITraceCollector
        // forces it into existence, and every capture seam resolves ITraceCollector — so the listener
        // exists by the time anything can have recorded, however the graph is first touched. Doing it
        // this way rather than through an IHostedService keeps it working in a console app or a test,
        // which is where the correlation this listener commits on was broken to begin with.
        services.AddSingleton<ITraceCollector>(static sp =>
        {
            _ = sp.GetRequiredService<StageActivityListener>();

            return sp.GetRequiredService<TraceCollector>();
        });
    }

    /// <summary>Registers the seams that capture retrieval and the assembled prompt.</summary>
    /// <param name="services">The service collection.</param>
    private static void RegisterCaptureSeams(IServiceCollection services)
    {
        services.AddSingleton<DiagnosticsRetrievalBehavior>(static sp => new DiagnosticsRetrievalBehavior(
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetService<ILogger<DiagnosticsRetrievalBehavior>>()));

        // First in the pipeline, so it observes the results every other behavior has finished with —
        // the position AuditRetrievalBehavior takes, for the same reason.
        // Named for the failure message: WireTracing is reached from AddRagDiagnostics rather
        // than written by the user, so it is that call they need to move after AddRagNet.
        services.RagRetrievalPipeline(nameof(AddRagDiagnostics)).AddFirst<DiagnosticsRetrievalBehavior>();

        services.AddSingleton<IPromptObserver>(static sp => new TracePromptObserver(
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetService<ILogger<TracePromptObserver>>()));
    }

    /// <summary>Wraps the guards, sanitisers and answer engine that are already registered.</summary>
    /// <param name="services">The service collection.</param>
    private static void DecorateWhatIsRegistered(IServiceCollection services)
    {
        ServiceDecoration.DecorateAll<IRetrievalGuard>(services, static (inner, sp) => new TracingRetrievalGuard(
            inner,
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetRequiredService<RagTraceOptions>(),
            sp.GetService<ILogger<TracingRetrievalGuard>>()));

        ServiceDecoration.DecorateAll<IChunkSanitiser>(services, static (inner, sp) => new TracingChunkSanitiser(
            inner,
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetService<ILogger<TracingChunkSanitiser>>()));

        ServiceDecoration.DecorateAll<IQuerySanitiser>(services, static (inner, sp) => new TracingQuerySanitiser(
            inner,
            sp.GetRequiredService<ITraceCollector>(),
            sp.GetService<ILogger<TracingQuerySanitiser>>()));

        // Not decorated like the three above: the answer engine is composed when the pipeline is
        // resolved, so this wraps whatever the container ends up answering with — an engine
        // registered later, or the ChatAnswerEngine built from a chat client registered later.
        services.RagAnswerEngineDecorations(nameof(AddRagDiagnostics))
            .Add(nameof(AddRagDiagnostics), DecorateAnswerEngine);
    }

    /// <summary>Wraps one answer engine so the answers it generates are recorded.</summary>
    /// <param name="inner">The engine being wrapped.</param>
    /// <param name="serviceProvider">The provider, for the collector and the logger.</param>
    /// <returns>The decorated engine.</returns>
    private static IAnswerEngine DecorateAnswerEngine(IAnswerEngine inner, IServiceProvider serviceProvider) =>
        new DiagnosticsAnswerEngineDecorator(
            inner,
            serviceProvider.GetRequiredService<ITraceCollector>(),
            serviceProvider.GetService<ILogger<DiagnosticsAnswerEngineDecorator>>());
}
