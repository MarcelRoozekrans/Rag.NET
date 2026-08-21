using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>Registration for A/B shadow mode.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IRagPipeline"/> in shadow mode: the caller always gets
    /// the primary's answer, and a sampled share of <c>AskAsync</c> calls also runs
    /// <typeparamref name="TSecondary"/> out-of-band, persisting the pair for offline scoring.
    /// </summary>
    /// <typeparam name="TSecondary">
    /// The pipeline to shadow. Registered as a singleton unless the application already
    /// registered one — build it however the variant under evaluation differs: chunking, vector
    /// store, embedding model, reranker.
    /// </typeparam>
    /// <param name="builder">The RAG builder. Requires an <see cref="IRagPipeline"/> to already be registered.</param>
    /// <param name="configure">
    /// Optional. <b>Without it shadow mode does nothing</b>: <see cref="ShadowPipelineOptions.SampleRate"/>
    /// defaults to 0, because every sampled request roughly doubles that request's spend and
    /// nobody should discover a doubled bill by upgrading a package. An out-of-range rate throws
    /// here, at registration, rather than being clamped to a rate nobody chose.
    /// </param>
    /// <returns>
    /// The builder, so this call chains into the next one. The static type is
    /// <see cref="IRagBuilder"/> rather than <c>RagBuilder</c> — this package depends on
    /// <c>Rag.NET.Abstractions</c> alone, and needs nothing here beyond
    /// <see cref="IRagBuilder.Services"/>. The satellites' <c>Use*</c> extensions accept any
    /// <see cref="IRagBuilder"/>, so <c>rag.UseShadow&lt;T&gt;().UseGraphRag()</c> composes.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Call this after the pipeline exists</b> — the same ordering rule <c>AddRagDiagnostics</c>
    /// carries: it decorates the last <see cref="IRagPipeline"/> registration present at the time
    /// of the call, which is what <c>AddRagNet</c> puts there. Calling it first is refused rather
    /// than left to fail at resolution time.
    /// </para>
    /// <para>
    /// <b>Captured pairs hold production data verbatim</b> — the user's question and the retrieved
    /// document text, exactly as they were. The default store is the in-memory
    /// <see cref="InMemoryShadowCaptureStore"/>; register an <see cref="IShadowCaptureStore"/>
    /// before this call to persist elsewhere, and see the store seam's documentation for what
    /// retention and protection remain the application's responsibility.
    /// </para>
    /// <para>
    /// Also honoured if registered beforehand: an <see cref="IShadowCaptureSanitiser"/> —
    /// <b>without one, captures persist verbatim</b>, a deliberate default documented on the
    /// seam itself — plus <see cref="ShadowCaptureQueueOptions"/> (queue capacity) and
    /// <see cref="ShadowCaptureConsumerOptions"/> (shutdown drain timeout, and the dedicated
    /// cost ledger that makes the secondary's per-request spend visible in each capture).
    /// </para>
    /// <para>
    /// The capture consumer is registered as an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    /// — the same instance that is resolvable directly, so its counters
    /// (<see cref="ShadowCaptureConsumer.AbandonedCount"/>, <see cref="ShadowCaptureConsumer.FailureSnapshot"/>)
    /// read from the consumer the host actually ran. In a host that never starts hosted services,
    /// nothing consumes the queue: captures accumulate up to its capacity and then drop, counted.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The configured sample rate is out of range.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="IRagPipeline"/> is registered.</exception>
    public static IRagBuilder UseShadow<TSecondary>(
        this IRagBuilder builder,
        Action<ShadowPipelineOptions>? configure = null)
        where TSecondary : class, IRagPipeline
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ShadowPipelineOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        RegisterCaptureInfrastructure(services, options);
        services.TryAddSingleton<TSecondary>();
        DecorateThePipeline<TSecondary>(services, options);

        return builder;
    }

    /// <summary>Registers the queue, the store default, and the hosted consumer.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The configured options, already validated by their own setters.</param>
    private static void RegisterCaptureInfrastructure(IServiceCollection services, ShadowPipelineOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton<ShadowCaptureQueueOptions>();
        services.TryAddSingleton<ShadowCaptureConsumerOptions>();
        services.TryAddSingleton(static sp =>
            new ShadowCaptureQueue(sp.GetRequiredService<ShadowCaptureQueueOptions>()));
        services.TryAddSingleton<IShadowCaptureStore, InMemoryShadowCaptureStore>();

        services.TryAddSingleton(static sp => new ShadowCaptureConsumer(
            sp.GetRequiredService<ShadowCaptureQueue>(),
            sp.GetRequiredService<IShadowCaptureStore>(),
            sp.GetRequiredService<ShadowCaptureConsumerOptions>(),
            sp.GetService<IShadowCaptureSanitiser>(),
            sp.GetService<ILogger<ShadowCaptureConsumer>>()));
        services.AddHostedService(static sp => sp.GetRequiredService<ShadowCaptureConsumer>());
    }

    /// <summary>Wraps the last <see cref="IRagPipeline"/> registration in the shadow decorator.</summary>
    /// <typeparam name="TSecondary">The pipeline to shadow.</typeparam>
    /// <param name="services">The collection to rewrite.</param>
    /// <param name="options">The configured options the decorator samples by.</param>
    /// <remarks>
    /// The last registration, because <see cref="IRagPipeline"/> is resolved as a single instance
    /// — the idiom <c>ConfigureResilience</c> uses for <c>IVectorStore</c>. Ownership follows
    /// <c>ServiceDecoration</c>'s rules: an inner materialised from a factory or a type is
    /// re-registered under a unique key so the container still disposes it; an instance
    /// registration stays externally owned.
    /// </remarks>
    private static void DecorateThePipeline<TSecondary>(IServiceCollection services, ShadowPipelineOptions options)
        where TSecondary : class, IRagPipeline
    {
        var original = FindLastPipelineRegistration(services)
            ?? throw new InvalidOperationException(
                "UseShadow requires an IRagPipeline to already be registered — call AddRagNet " +
                "first (UseShadow decorates the registration that exists at the time of the " +
                "call). Registered first, there would be nothing to shadow and the wrapper " +
                "would silently never run.");

        services.Remove(original);

        if (original.ImplementationInstance is IRagPipeline instance)
        {
            services.Add(ServiceDescriptor.Describe(
                typeof(IRagPipeline), sp => Shadowed<TSecondary>(instance, sp, options), original.Lifetime));
            return;
        }

        var innerKey = string.Create(
            CultureInfo.InvariantCulture,
            $"ragnet.evaluation.shadow.inner.{Guid.NewGuid():N}");

        services.Add(ServiceDescriptor.DescribeKeyed(
            typeof(IRagPipeline), innerKey, (sp, _) => Materialise(sp, original), original.Lifetime));
        services.Add(ServiceDescriptor.Describe(
            typeof(IRagPipeline),
            sp => Shadowed<TSecondary>(sp.GetRequiredKeyedService<IRagPipeline>(innerKey), sp, options),
            original.Lifetime));
    }

    /// <summary>Builds the decorator around one materialised primary.</summary>
    /// <typeparam name="TSecondary">The pipeline to shadow.</typeparam>
    /// <param name="primary">The pipeline the caller is served by.</param>
    /// <param name="serviceProvider">The provider, for the secondary, the queue, the clock and the logger.</param>
    /// <param name="options">The configured options.</param>
    /// <returns>The shadowed pipeline.</returns>
    private static ShadowRagPipeline Shadowed<TSecondary>(
        IRagPipeline primary,
        IServiceProvider serviceProvider,
        ShadowPipelineOptions options)
        where TSecondary : class, IRagPipeline =>
        new(
            primary,
            serviceProvider.GetRequiredService<TSecondary>(),
            serviceProvider.GetRequiredService<ShadowCaptureQueue>(),
            options,
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<ILogger<ShadowRagPipeline>>());

    /// <summary>The most recent non-keyed <see cref="IRagPipeline"/> descriptor, or null.</summary>
    /// <param name="services">The collection to scan.</param>
    private static ServiceDescriptor? FindLastPipelineRegistration(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var candidate = services[i];
            if (!candidate.IsKeyedService && candidate.ServiceType == typeof(IRagPipeline))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Builds what the original descriptor would have built.</summary>
    /// <param name="serviceProvider">The provider to resolve dependencies from.</param>
    /// <param name="descriptor">The original descriptor.</param>
    /// <returns>The undecorated pipeline.</returns>
    private static IRagPipeline Materialise(IServiceProvider serviceProvider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationFactory is { } factory
            ? (IRagPipeline)factory(serviceProvider)
            : (IRagPipeline)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!);
}
