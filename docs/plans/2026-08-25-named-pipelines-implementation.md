# Named Pipelines Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** let one container hold several isolated RAG pipelines — `AddRagNet("docs", …)` and `AddRagNet("support", …)` — each with its own stores, reached through `IRagPipelineFactory.Get(name)`, while expensive services declared in `AddRagNetShared(…)` stay singular.

**Architecture:** each named block composes into its own `IServiceCollection` and builds a child `ServiceProvider` lazily on first `Get(name)`. Shared services are forwarded into each child as descriptors that resolve from the root provider. The unnamed `AddRagNet` is untouched.

**Tech Stack:** .NET 10, C#, `Microsoft.Extensions.DependencyInjection`, xUnit v3 (via Microsoft.Testing.Platform), NSubstitute.

**Spec:** [`docs/plans/2026-08-25-named-pipelines-design.md`](./2026-08-25-named-pipelines-design.md)

## Global Constraints

- **`TreatWarningsAsErrors=true`** in `Directory.Build.props`. A warning fails the build. Public members require complete XML doc comments.
- **`MA0051` caps methods at 60 lines.** Meziantou's analyzer is on; extract rather than suppress.
- **Commit headers must be ≤ 100 characters.** `.commitlintrc.yml` gates CI on every commit a PR adds, and `body-max-line-length` is off, so detail belongs in the body.
- **`dotnet test --filter` is silently ignored** in this repo (xunit v3 via Microsoft.Testing.Platform). Run whole test projects, or use the runner's own `-class '*Name*'` against the built executable.
- **The unnamed `AddRagNet` must not change behaviour.** `docs/guide/vector-stores.md:211`, `:346`, `:544` and `getting-started.md:83` resolve pipeline internals straight from the root provider. The existing suite is the regression guard.
- **`Rag.NET.Abstractions` is a published package.** `IRagPipelineFactory` goes there and is a permanent surface commitment.

---

### Task 1: `IRagPipelineFactory` and its shared-service bookkeeping

**Files:**
- Create: `src/Rag.NET.Abstractions/Abstractions/IRagPipelineFactory.cs`
- Create: `src/Rag.NET/DependencyInjection/SharedServiceTypes.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/SharedServiceTypesTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public interface IRagPipelineFactory { IRagPipeline Get(string name); bool Contains(string name); }` in namespace `Rag.NET.Abstractions`.
  - `internal sealed class SharedServiceTypes { public IReadOnlyList<Type> Types { get; } ; public void AddRange(IEnumerable<Type> types); }` in namespace `Rag.NET.DependencyInjection`. Tasks 2 and 3 both depend on this exact shape.

- [ ] **Step 1: Write the failing test**

Create `tests/Rag.NET.Tests/DependencyInjection/SharedServiceTypesTests.cs`:

```csharp
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class SharedServiceTypesTests
{
    [Fact]
    public void Types_WhenNothingAdded_IsEmpty()
    {
        var sut = new SharedServiceTypes();

        Assert.Empty(sut.Types);
    }

    [Fact]
    public void AddRange_RecordsEachType()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([typeof(string), typeof(int)]);

        Assert.Equal([typeof(string), typeof(int)], sut.Types);
    }

    // Two AddRagNetShared calls are legal; the second must not lose the first's types.
    [Fact]
    public void AddRange_CalledTwice_KeepsBoth()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([typeof(string)]);
        sut.AddRange([typeof(int)]);

        Assert.Equal([typeof(string), typeof(int)], sut.Types);
    }

    // The same service type declared shared twice must forward once, or the child collection
    // gets duplicate descriptors and IEnumerable<T> resolution silently doubles.
    [Fact]
    public void AddRange_WithADuplicateType_RecordsItOnce()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([typeof(string)]);
        sut.AddRange([typeof(string)]);

        Assert.Equal([typeof(string)], sut.Types);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build tests/Rag.NET.Tests -c Release`

Expected: FAIL to compile — `SharedServiceTypes` does not exist. A compile failure is the correct red here; do not proceed until you have seen it.

- [ ] **Step 3: Create `IRagPipelineFactory`**

Create `src/Rag.NET.Abstractions/Abstractions/IRagPipelineFactory.cs`:

```csharp
namespace Rag.NET.Abstractions;

/// <summary>Resolves the named <see cref="IRagPipeline"/>s registered by <c>AddRagNet(name, …)</c>.</summary>
/// <remarks>
/// <para>
/// Each named pipeline has its own service provider, so its vector store, BM25 index, caches and
/// behaviours are separate from every other name's. Services declared through
/// <c>AddRagNetShared</c> — an embedding model, an <c>IChatClient</c> — stay singular across all of
/// them.
/// </para>
/// <para>
/// The unnamed <c>AddRagNet</c> is unaffected: it registers into the root container and its pipeline
/// is still resolved with <c>GetRequiredService&lt;IRagPipeline&gt;()</c>. Named pipelines are
/// additive (#342).
/// </para>
/// </remarks>
public interface IRagPipelineFactory
{
    /// <summary>Gets the pipeline registered under <paramref name="name"/>.</summary>
    /// <param name="name">The name passed to <c>AddRagNet(name, …)</c>.</param>
    /// <returns>That name's pipeline. The same instance on every call.</returns>
    /// <exception cref="ArgumentException">No pipeline was registered under that name.</exception>
    IRagPipeline Get(string name);

    /// <summary>Whether a pipeline was registered under <paramref name="name"/>.</summary>
    /// <param name="name">The name to look for.</param>
    /// <returns>Whether <see cref="Get"/> would succeed.</returns>
    bool Contains(string name);
}
```

- [ ] **Step 4: Create `SharedServiceTypes`**

Create `src/Rag.NET/DependencyInjection/SharedServiceTypes.cs`:

```csharp
namespace Rag.NET.DependencyInjection;

/// <summary>
/// The service types <c>AddRagNetShared</c> declared, which every named pipeline forwards to the
/// root provider instead of registering for itself.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton on the root collection so that <c>AddRagNet(name, …)</c> can read it
/// regardless of call order — a named block may be declared before the shared block.
/// </para>
/// <para>
/// A set rather than a list, because declaring the same service type shared twice must forward once:
/// duplicate descriptors in a child collection make <c>IEnumerable&lt;T&gt;</c> resolution return it
/// twice, which is the kind of silent doubling that does not fail until something counts.
/// </para>
/// </remarks>
internal sealed class SharedServiceTypes
{
    private readonly List<Type> _types = [];
    private readonly HashSet<Type> _seen = [];

    /// <summary>The declared types, in declaration order.</summary>
    public IReadOnlyList<Type> Types => _types;

    /// <summary>Records <paramref name="types"/>, ignoring any already recorded.</summary>
    /// <param name="types">Service types declared shared.</param>
    public void AddRange(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        foreach (var type in types)
        {
            if (_seen.Add(type))
            {
                _types.Add(type);
            }
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS, including all four `SharedServiceTypesTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IRagPipelineFactory.cs src/Rag.NET/DependencyInjection/SharedServiceTypes.cs tests/Rag.NET.Tests/DependencyInjection/SharedServiceTypesTests.cs
git commit -m "feat(di): add IRagPipelineFactory and shared-service bookkeeping (#342)"
```

---

### Task 2: `AddRagNetShared`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/AddRagNetSharedTests.cs`

**Interfaces:**
- Consumes: `SharedServiceTypes` from Task 1.
- Produces: `public static IServiceCollection AddRagNetShared(this IServiceCollection services, Action<RagBuilder> configure)`. Task 3 reads the `SharedServiceTypes` singleton this registers.

- [ ] **Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/DependencyInjection/AddRagNetSharedTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class AddRagNetSharedTests
{
    private interface IThing;

    private sealed class Thing : IThing;

    [Fact]
    public void AddRagNetShared_RegistersIntoTheRootCollection()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<Thing>(provider.GetRequiredService<IThing>());
    }

    [Fact]
    public void AddRagNetShared_RecordsTheServiceTypesItRegistered()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<SharedServiceTypes>();
        Assert.Contains(typeof(IThing), shared.Types);
    }

    /// <summary>
    /// It records only what its own callback added, not what was already on the collection.
    /// </summary>
    /// <remarks>
    /// The root collection also holds the host's logging, configuration and HttpClients. Forwarding
    /// those into every child would make each pipeline depend on the host's container shape, which
    /// is exactly why sharing is a declared block rather than inferred from the outer collection.
    /// </remarks>
    [Fact]
    public void AddRagNetShared_DoesNotRecordPreexistingRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IThing, Thing>();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<Thing>());

        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<SharedServiceTypes>();
        Assert.DoesNotContain(typeof(IThing), shared.Types);
        Assert.Contains(typeof(Thing), shared.Types);
    }

    /// <summary>
    /// It does not register a pipeline. Sharing a model is not running a pipeline in the root.
    /// </summary>
    /// <remarks>
    /// If this called <c>AddRagNETServices()</c> the root would build its own stores alongside every
    /// child's — paying for a pipeline nobody asked for, and muddying which store a forwarded
    /// resolve reaches.
    /// </remarks>
    [Fact]
    public void AddRagNetShared_DoesNotRegisterAPipeline()
    {
        var services = new ServiceCollection();

        services.AddRagNetShared(rag => rag.Services.AddSingleton<IThing, Thing>());

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IRagPipeline>());
    }
}
```

Add `using Rag.NET.Abstractions;` for `IRagPipeline`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/Rag.NET.Tests -c Release`

Expected: FAIL to compile — `AddRagNetShared` does not exist.

- [ ] **Step 3: Implement `AddRagNetShared`**

Append to `ServiceCollectionExtensions`:

```csharp
    /// <summary>
    /// Declares services shared by every named pipeline — an embedding model, an
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/>, anything expensive enough that one per
    /// pipeline would be wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the same <see cref="RagBuilder"/> and the same <c>Use*</c> methods as <c>AddRagNet</c>,
    /// because <see cref="RagBuilder"/> is a wrapper over an <see cref="IServiceCollection"/> and
    /// nothing about those methods is tied to a pipeline being registered.
    /// </para>
    /// <para>
    /// <b>It deliberately does not register a pipeline.</b> Sharing a model is not the same as
    /// running a pipeline in the root container: one that did would build its own stores alongside
    /// every child's.
    /// </para>
    /// <para>
    /// Four types hold an ONNX <c>InferenceSession</c> — the embedding generator, the token
    /// embedding generator, the SPLADE encoder and the reranker — and MiniLM alone is roughly 90 MB.
    /// Five named pipelines each calling <c>UseOnnxEmbeddings</c> would load it five times, which is
    /// the concrete reason this exists (#342).
    /// </para>
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <param name="configure">Registers the shared services, using the usual <c>Use*</c> methods.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRagNetShared(
        this IServiceCollection services, Action<RagBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Snapshot around the callback so only what it declared is forwarded. The root collection
        // also holds the host's own logging, configuration and HttpClients; forwarding those would
        // make every child depend on the host's container shape.
        var before = services.Count;
        configure(new RagBuilder(services));

        var declared = new List<Type>();
        for (var i = before; i < services.Count; i++)
        {
            declared.Add(services[i].ServiceType);
        }

        var shared = FindOrAddSharedServiceTypes(services);
        shared.AddRange(declared);
        return services;
    }

    /// <summary>
    /// Gets the collection's <see cref="SharedServiceTypes"/>, adding it on first use.
    /// </summary>
    /// <remarks>
    /// Held as a singleton <i>instance</i> so both <c>AddRagNetShared</c> and <c>AddRagNet(name, …)</c>
    /// see the same object at registration time, before any provider exists — and so a named block
    /// declared before the shared block still forwards correctly.
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <returns>The single instance for this collection.</returns>
    private static SharedServiceTypes FindOrAddSharedServiceTypes(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(SharedServiceTypes)
                && descriptor.ImplementationInstance is SharedServiceTypes existing)
            {
                return existing;
            }
        }

        var created = new SharedServiceTypes();
        services.AddSingleton(created);
        return created;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS.

- [ ] **Step 5: Mutation-check the snapshot**

Change `var before = services.Count;` to `var before = 0;` and re-run. Expected: `AddRagNetShared_DoesNotRecordPreexistingRegistrations` FAILS. **Confirm the build succeeded before reading that verdict** — a mutation that does not compile runs the previous binary and reports a false green. Restore by reversing the edit, not with `git checkout`, and re-run to confirm PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs tests/Rag.NET.Tests/DependencyInjection/AddRagNetSharedTests.cs
git commit -m "feat(di): add AddRagNetShared for services named pipelines share (#342)"
```

---

### Task 3: `AddRagNet(name, …)` and the factory implementation

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `src/Rag.NET/DependencyInjection/RagPipelineFactory.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/NamedPipelineTests.cs`

**Interfaces:**
- Consumes: `IRagPipelineFactory` and `SharedServiceTypes` (Task 1), `AddRagNetShared` (Task 2).
- Produces: `public static IServiceCollection AddRagNet(this IServiceCollection services, string name, Action<RagBuilder>? configure = null)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/DependencyInjection/NamedPipelineTests.cs`. Use the in-memory defaults so no external service is needed — `AddRagNet` composes a working pipeline with an in-memory store when one is registered. Register a substitute embedder and vector store per name so the test can tell them apart:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class NamedPipelineTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> Embedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        return embedder;
    }

    [Fact]
    public void Get_ReturnsTheSameInstanceEachTime()
    {
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.Same(factory.Get("docs"), factory.Get("docs"));
    }

    [Fact]
    public void Get_WithAnUnknownName_Throws()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        _ = Assert.Throws<ArgumentException>(() => factory.Get("absent"));
    }

    /// <summary>Two names get two pipelines, each reaching its own store.</summary>
    /// <remarks>The claim the feature exists to make. Without it nothing else here is verified.</remarks>
    [Fact]
    public void TwoNames_GetSeparatePipelinesWithSeparateStores()
    {
        var docsStore = Substitute.For<IVectorStore>();
        var supportStore = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(supportStore));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();

        Assert.NotSame(factory.Get("docs"), factory.Get("support"));
    }

    /// <summary>A shared service is one instance across every named pipeline.</summary>
    /// <remarks>
    /// The test that would catch descriptor copying duplicating an ONNX session: a type registration
    /// copied into two child collections constructs two instances, silently.
    /// </remarks>
    [Fact]
    public void ASharedService_IsTheSameInstanceInEveryNamedPipeline()
    {
        var embedder = Embedder();
        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(embedder));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.Get("docs");
        _ = factory.Get("support");

        // Resolved through each child, it is the one the root holds.
        Assert.Same(embedder, provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    /// <summary>A per-pipeline service is NOT shared — the converse of the test above.</summary>
    /// <remarks>
    /// Without this, "everything is shared" would satisfy the sharing test and the isolation the
    /// feature promises would be absent.
    /// </remarks>
    [Fact]
    public void APerPipelineService_IsNotSharedBetweenNames()
    {
        var docsStore = Substitute.For<IVectorStore>();
        var supportStore = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));
        services.AddRagNet("support", rag => rag.Services.AddSingleton(supportStore));

        using var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.Get("docs");
        _ = factory.Get("support");

        Assert.NotSame(
            factory.ProviderFor("docs").GetRequiredService<IVectorStore>(),
            factory.ProviderFor("support").GetRequiredService<IVectorStore>());
    }

    /// <summary>The unnamed pipeline and a named one coexist in one container.</summary>
    /// <remarks>
    /// The guarantee §1a of the spec makes: named pipelines are additive, and the documented root
    /// resolves keep working.
    /// </remarks>
    [Fact]
    public void UnnamedAndNamed_CoexistInOneContainer()
    {
        var rootStore = Substitute.For<IVectorStore>();
        var docsStore = Substitute.For<IVectorStore>();

        var services = new ServiceCollection();
        services.AddSingleton(Embedder());
        services.AddRagNet(rag => rag.Services.AddSingleton(rootStore));
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(docsStore));

        using var provider = services.BuildServiceProvider();

        Assert.Same(rootStore, provider.GetRequiredService<IVectorStore>());
        Assert.NotNull(provider.GetRequiredService<IRagPipeline>());
        Assert.NotNull(provider.GetRequiredService<IRagPipelineFactory>().Get("docs"));
    }
}
```

`ProviderFor(name)` is an internal test seam on `RagPipelineFactory`; Step 3 adds it. If `Rag.NET`'s csproj does not already grant `InternalsVisibleTo` to `Rag.NET.Tests`, check before assuming — it does for other internals used by this test project.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/Rag.NET.Tests -c Release`

Expected: FAIL to compile — the named `AddRagNet` overload and `RagPipelineFactory` do not exist.

- [ ] **Step 3: Implement `RagPipelineFactory`**

Create `src/Rag.NET/DependencyInjection/RagPipelineFactory.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.DependencyInjection;

/// <summary>Builds and owns one child <see cref="IServiceProvider"/> per named pipeline.</summary>
/// <remarks>
/// <para>
/// <b>Children are built lazily, on first <see cref="Get"/>.</b> At registration time the root
/// provider does not exist, so there is nothing for a child's forwarded services to resolve from.
/// The cost is that a misconfigured named pipeline surfaces on first use rather than at startup.
/// </para>
/// <para>
/// <b>Disposal is async and ownership runs one way.</b> Seven types in the per-pipeline surface are
/// <see cref="IAsyncDisposable"/>, and <c>ServiceProvider.Dispose()</c> throws when it holds a
/// service implementing only that. Shared services live in the root and are disposed by it, never by
/// a child — so tearing down one pipeline cannot pull the embedding model out from under another.
/// </para>
/// </remarks>
/// <param name="collections">Each name's composed service collection.</param>
/// <param name="rootProvider">The root provider, which forwarded services resolve from.</param>
internal sealed class RagPipelineFactory(
    IReadOnlyDictionary<string, IServiceCollection> collections,
    IServiceProvider rootProvider) : IRagPipelineFactory, IAsyncDisposable
{
    private readonly Dictionary<string, ServiceProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();  // matches InMemoryCostLedger.cs:17
    private bool _disposed;

    /// <inheritdoc />
    public bool Contains(string name) => collections.ContainsKey(name);

    /// <inheritdoc />
    public IRagPipeline Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ProviderFor(name).GetRequiredService<IRagPipeline>();
    }

    /// <summary>The child provider for <paramref name="name"/>, building it on first use.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <returns>That name's provider.</returns>
    /// <exception cref="ArgumentException">No pipeline was registered under that name.</exception>
    internal ServiceProvider ProviderFor(string name)
    {
        lock (_lock)
        {
            if (_providers.TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (!collections.TryGetValue(name, out var collection))
            {
                throw new ArgumentException(
                    $"No RAG pipeline is registered under the name '{name}'. "
                    + "Register one with services.AddRagNet(\"" + name + "\", rag => …).",
                    nameof(name));
            }

            var built = collection.BuildServiceProvider();
            _providers[name] = built;
            return built;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var provider in _providers.Values)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        _providers.Clear();
    }
}
```

`rootProvider` is unused until Step 4 wires forwarding; keep the parameter — Step 4 needs it and removing it would churn the signature twice.

- [ ] **Step 4: Implement the named `AddRagNet` overload**

Append to `ServiceCollectionExtensions`:

```csharp
    /// <summary>
    /// Registers a named RAG pipeline with its own service provider, reached through
    /// <see cref="IRagPipelineFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The named block composes into its own <see cref="IServiceCollection"/>, so its vector store,
    /// BM25 index, caches and behaviours are separate from every other name's. Every <c>Use*</c>
    /// method works unchanged, because they all operate on a collection.
    /// </para>
    /// <para>
    /// Service types declared through <see cref="AddRagNetShared"/> are forwarded to the root
    /// provider rather than registered again, so one embedding model serves every pipeline.
    /// </para>
    /// <para>
    /// The unnamed <c>AddRagNet</c> is unaffected and still registers into the root container (#342).
    /// </para>
    /// </remarks>
    /// <param name="services">The root service collection.</param>
    /// <param name="name">The pipeline's name, passed later to <see cref="IRagPipelineFactory.Get"/>.</param>
    /// <param name="configure">Configures this pipeline, with the usual <c>Use*</c> methods.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRagNet(
        this IServiceCollection services, string name, Action<RagBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var shared = FindOrAddSharedServiceTypes(services);
        var named = FindOrAddNamedCollections(services);
        if (named.ContainsKey(name))
        {
            throw new ArgumentException(
                $"A RAG pipeline named '{name}' is already registered.", nameof(name));
        }

        var inner = new ServiceCollection();
        _ = inner.AddRagNet(configure);
        named[name] = new NamedPipelineRegistration(inner, shared);

        services.TryAddSingleton<IRagPipelineFactory>(sp => BuildFactory(named, sp));
        return services;
    }
```

Add the two helpers beside it — `FindOrAddNamedCollections` mirrors `FindOrAddSharedServiceTypes` from Task 2, and `BuildFactory` applies the forwarding:

```csharp
    /// <summary>Applies shared-service forwarding and constructs the factory.</summary>
    /// <remarks>
    /// Forwarding happens here, not at registration: the descriptors close over the root provider,
    /// which only exists once the container is built.
    /// </remarks>
    /// <param name="named">Each name's registration.</param>
    /// <param name="rootProvider">The root provider forwarded services resolve from.</param>
    /// <returns>The factory.</returns>
    private static RagPipelineFactory BuildFactory(
        Dictionary<string, NamedPipelineRegistration> named, IServiceProvider rootProvider)
    {
        var collections = new Dictionary<string, IServiceCollection>(StringComparer.Ordinal);
        foreach (var (name, registration) in named)
        {
            foreach (var serviceType in registration.Shared.Types)
            {
                registration.Services.Replace(
                    ServiceDescriptor.Singleton(serviceType, _ => rootProvider.GetRequiredService(serviceType)));
            }

            collections[name] = registration.Services;
        }

        return new RagPipelineFactory(collections, rootProvider);
    }

    /// <summary>One named pipeline's composed collection and the shared types it forwards.</summary>
    /// <param name="Services">The inner collection this name composed into.</param>
    /// <param name="Shared">The shared-type registry, read at build time so call order does not matter.</param>
    private sealed record NamedPipelineRegistration(IServiceCollection Services, SharedServiceTypes Shared);
```

`Replace` (from `Microsoft.Extensions.DependencyInjection.Extensions`) is deliberate rather than `Add`: the inner `AddRagNet` may already have registered the same service type, and adding a second descriptor would leave the child resolving its own instance for `GetRequiredService` while `IEnumerable<T>` returned both.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS, including all six `NamedPipelineTests`.

- [ ] **Step 6: Mutation-check the sharing**

Change `registration.Services.Replace(` to `registration.Services.Add(` and re-run. Expected: `ASharedService_IsTheSameInstanceInEveryNamedPipeline` FAILS, because the child's own registration wins. **Confirm the build succeeded first.** Restore by reversing the edit and re-run.

- [ ] **Step 7: Run the whole solution**

Run: `dotnet build Rag.NET.slnx -c Release` then `dotnet test tests/Rag.NET.Tests -c Release --no-build`

Expected: 0 warnings, 0 errors, all tests passing. The existing suite is the regression guard for the unnamed path.

- [ ] **Step 8: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagPipelineFactory.cs src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs tests/Rag.NET.Tests/DependencyInjection/NamedPipelineTests.cs
git commit -m "feat(di): add AddRagNet(name, …) with per-pipeline providers (#342)"
```

---

### Task 4: Disposal

**Files:**
- Create: `tests/Rag.NET.Tests/DependencyInjection/NamedPipelineDisposalTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class NamedPipelineDisposalTests
{
    private sealed class AsyncOnlyDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static IEmbeddingGenerator<string, Embedding<float>> Embedder() =>
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    /// <summary>Disposing the factory disposes each child's async-only services, and does not throw.</summary>
    /// <remarks>
    /// <c>ServiceProvider.Dispose()</c> throws when it holds a service implementing only
    /// <see cref="IAsyncDisposable"/>. Seven types in the per-pipeline surface do, so getting this
    /// wrong is a crash at shutdown rather than a leak.
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DisposesEachChildsAsyncOnlyServices()
    {
        var docsResource = new AsyncOnlyDisposable();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag => rag.Services.AddSingleton(Embedder()));
        services.AddRagNet("docs", rag =>
        {
            rag.Services.AddSingleton(Substitute.For<IVectorStore>());
            rag.Services.AddSingleton(docsResource);
        });

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        await factory.DisposeAsync();

        Assert.True(docsResource.Disposed);
    }

    /// <summary>A shared service is not disposed by a child.</summary>
    /// <remarks>
    /// Ownership runs one way. If a child disposed what it merely forwards, tearing down one
    /// pipeline would pull the embedding model out from under every other one.
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_DoesNotDisposeSharedServices()
    {
        var sharedResource = new AsyncOnlyDisposable();

        var services = new ServiceCollection();
        services.AddRagNetShared(rag =>
        {
            rag.Services.AddSingleton(Embedder());
            rag.Services.AddSingleton(sharedResource);
        });
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();
        _ = factory.ProviderFor("docs").GetRequiredService<AsyncOnlyDisposable>();

        await factory.DisposeAsync();

        Assert.False(sharedResource.Disposed);

        await provider.DisposeAsync();
        Assert.True(sharedResource.Disposed);
    }

    [Fact]
    public async Task Get_AfterDispose_Throws()
    {
        var services = new ServiceCollection();
        services.AddRagNet("docs", rag => rag.Services.AddSingleton(Substitute.For<IVectorStore>()));

        var provider = services.BuildServiceProvider();
        var factory = (RagPipelineFactory)provider.GetRequiredService<IRagPipelineFactory>();

        await factory.DisposeAsync();

        _ = Assert.Throws<ObjectDisposedException>(() => factory.Get("docs"));
        await provider.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Rag.NET.Tests -c Release`

Expected: PASS if Task 3's factory is correct. **If `DisposeAsync_DoesNotDisposeSharedServices` fails**, the forwarding descriptor is registering an owned instance rather than a resolve-from-root factory — fix the forwarding, not the test: a child must never own what it forwards.

- [ ] **Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/DependencyInjection/NamedPipelineDisposalTests.cs
git commit -m "test(di): pin named-pipeline disposal ownership and async disposal (#342)"
```

---

### Task 5: Document it

**Files:**
- Modify: `docs/guide/architecture.md` (locate the composition section with `grep -n "AddRagNet" docs/guide/architecture.md`)
- Modify: `README.md`

**Interfaces:**
- Consumes: the completed feature.
- Produces: nothing.

- [ ] **Step 1: Document the named form**

Add this to `docs/guide/architecture.md`, placed where composition is described (find it with `grep -n "AddRagNet" docs/guide/architecture.md`) and adapted to that page's heading level and voice:

```markdown
### Named pipelines

One container can hold several isolated pipelines, each with its own stores:

```csharp
services.AddRagNetShared(rag => rag.UseOnnxEmbeddings(o => { ... }));   // one model, shared
services.AddRagNet("docs",    rag => rag.UseAzureAISearch(endpoint, "docs-index",    credential));
services.AddRagNet("support", rag => rag.UseAzureAISearch(endpoint, "support-index", credential));

var docs = provider.GetRequiredService<IRagPipelineFactory>().Get("docs");
```

Each name gets its own vector store, BM25 index, caches and behaviours. Services declared in
`AddRagNetShared` stay singular — four types hold an ONNX `InferenceSession`, and one per pipeline
would load the same model repeatedly.

**`AddRagNet(rag => ...)` is unchanged.** It still registers into the root container and its pipeline
is still resolved with `GetRequiredService<IRagPipeline>()`. Named pipelines are additive; the two
forms coexist in one container.

**Why not a metadata filter?** `RetrievalOptions.MetadataFilter` is caller-supplied per query, so
forgetting it once is a cross-read. An index binding cannot be forgotten. Isolation belongs in the
registration.

**Named pipelines are built on first `Get(name)`**, not at startup — a misconfigured one surfaces
then rather than when the container is built.
```

Then add a short pointer in `README.md` beside the existing composition example, linking to that section rather than repeating it.

- [ ] **Step 2: Verify docs conventions**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/ README.md
git commit -m "docs(di): document named pipelines and shared services (#342)"
```

---

## Definition of Done

- [ ] `AddRagNet(rag => …)` behaviour is byte-identical — the existing suite passes untouched.
- [ ] Two named pipelines resolve different `IVectorStore` instances.
- [ ] A service declared in `AddRagNetShared` is one instance across every named pipeline, verified by reference equality.
- [ ] A per-pipeline service is **not** shared — so the sharing test cannot pass by everything being shared.
- [ ] Disposing the factory disposes each child's async-only services and does not throw.
- [ ] Disposing a child does **not** dispose shared services; the root does.
- [ ] The unnamed and named forms coexist in one container.
- [ ] `dotnet build Rag.NET.slnx -c Release` is clean, and every commit header is ≤ 100 characters.
