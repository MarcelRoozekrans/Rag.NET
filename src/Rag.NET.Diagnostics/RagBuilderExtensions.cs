using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
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
    /// <b>Call this last.</b> It reads the service collection <i>at the time of the call</i> — the same
    /// ordering rule <c>ConfigureResilience</c> and <c>UseRateLimiting</c> carry. It decorates the
    /// <c>IRetrievalGuard</c>, <c>IChunkSanitiser</c>, <c>IQuerySanitiser</c> and <c>IAnswerEngine</c>
    /// registrations that exist by then, and it also <b>looks for an <c>IChatClient</c></b>. A guard
    /// registered afterwards still runs; it is simply not traced, and its absence from a trace then
    /// means "not registered when diagnostics was" rather than "never fired", which is a misleading
    /// answer to the question traces exist to answer.
    /// </para>
    /// <para>
    /// <b>The <c>IChatClient</c> case reads as a bug rather than a misconfiguration</b>, so it is worth
    /// recognising. With no <c>IAnswerEngine</c> registered, this method supplies a traced one only if
    /// an <c>IChatClient</c> is already in the collection — see
    /// <see cref="AddAnswerEngineIfAChatClientIsRegistered"/>. Register the chat client afterwards, as
    /// in <c>AddRagNet(rag => rag.AddRagDiagnostics())</c> followed by
    /// <c>services.AddSingleton&lt;IChatClient&gt;(…)</c>, and there is nothing to find: no
    /// <c>IAnswerEngine</c> is registered, <c>RagPipeline</c> builds its own untraced one, and answers
    /// are never recorded. <b>The symptom</b> is a trace that has a query, chunks, stages and a prompt
    /// but whose <c>Answer</c> is always <see langword="null"/> even with
    /// <see cref="RagTraceOptions.CaptureAnswerText"/> on — while asking still returns an answer
    /// perfectly well. Register the <c>IChatClient</c> first and it is captured.
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
        RetrievalPipelineBuilderIn(services).AddFirst<DiagnosticsRetrievalBehavior>();

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

        if (ServiceDecoration.DecorateAll<IAnswerEngine>(services, DecorateAnswerEngine) == 0)
            AddAnswerEngineIfAChatClientIsRegistered(services);
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

    /// <summary>Supplies a traced answer engine when the pipeline was going to build its own.</summary>
    /// <param name="services">The service collection.</param>
    /// <remarks>
    /// <para>
    /// The common shape — <c>AddRagNet</c> plus an <c>IChatClient</c>, with none of the security
    /// package's answer-engine decorators — registers <b>no</b> <c>IAnswerEngine</c> at all:
    /// <c>AddRagNet</c>'s <c>RagPipeline</c> factory calls <c>ChatAnswerEngine.CreateFromServices</c>
    /// itself when it finds none. There is nothing there to decorate, so without this the headline
    /// capability — capturing the answer — would be missing from exactly the setup most people have.
    /// </para>
    /// <para>
    /// Gated on an <c>IChatClient</c> being registered, because <c>CreateFromServices</c> requires one
    /// and would otherwise turn a retrieval-only pipeline — which has no answer engine on purpose —
    /// into one that throws the moment <c>RagPipeline</c> is resolved. Registering diagnostics must
    /// never be able to break a pipeline that worked without it.
    /// </para>
    /// </remarks>
    private static void AddAnswerEngineIfAChatClientIsRegistered(IServiceCollection services)
    {
        if (!services.Any(static d => d.ServiceType == typeof(IChatClient)))
            return;

        services.AddSingleton<IAnswerEngine>(static sp =>
            DecorateAnswerEngine(ChatAnswerEngine.CreateFromServices(sp), sp));
    }

    /// <summary>Finds the retrieval pipeline builder <c>AddRagNet</c> put in the collection.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The builder, so a behavior can be inserted into the chain.</returns>
    /// <exception cref="InvalidOperationException"><c>AddRagNet</c> has not been called.</exception>
    private static RetrievalPipelineBuilder RetrievalPipelineBuilderIn(IServiceCollection services) =>
        services.FirstOrDefault(static d => d.ServiceType == typeof(RetrievalPipelineBuilder))
                ?.ImplementationInstance as RetrievalPipelineBuilder
        ?? throw new InvalidOperationException(
            "AddRagDiagnostics requires AddRagNet to have been called first, so that the " +
            "RetrievalPipelineBuilder the retrieval behavior is inserted into exists. Call it from " +
            "inside AddRagNet's configure delegate, or on a RagBuilder built afterwards.");
}
