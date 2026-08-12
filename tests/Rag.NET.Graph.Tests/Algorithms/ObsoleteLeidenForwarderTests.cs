using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rag.NET.Graph;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

/// <summary>
/// Exercises the <c>Leiden</c> and <c>LeidenOptions</c> forwarders left behind when the clusterer
/// was renamed to <see cref="LouvainWithRefinement"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A forwarder nobody has run is indistinguishable from a broken one</b>, and this repository
/// has found that shape before — a validator whose only caller was a method production never
/// invoked. So these tests call through the deprecated names rather than asserting that they
/// compile.
/// </para>
/// <para>
/// <b>They call through by reflection, and that is forced.</b> This tree builds with
/// <c>TreatWarningsAsErrors</c>, so a test that named <c>Leiden</c> in source would turn its own
/// deprecation warning into a build error — the forwarder cannot be exercised the way a consumer
/// writes it. Reflection reaches the same method body: <see cref="Detect"/> on the forwarder is
/// what runs, and if it stopped delegating, or delegated with different arguments, the partition
/// comparison below would part company with the new type's.
/// </para>
/// <para>
/// <b>What a consumer actually sees is measured by compiling one.</b>
/// <see cref="AConsumerOfTheOldNamesGetsAWarningAndNotAnError"/> runs the C# compiler over a small
/// program written against <c>Leiden</c> and <c>LeidenOptions</c> and asserts CS0618 — the warning
/// — with no error of any kind. That is the claim the <c>[Obsolete]</c> attributes make, and
/// <c>error: true</c> on either of them, or a forwarder that no longer compiles against the type it
/// forwards to, fails here rather than at some consumer's next upgrade.
/// </para>
/// </remarks>
public class ObsoleteLeidenForwarderTests
{
    private const string ObsoleteClustererTypeName = "Rag.NET.Graph.Algorithms.Leiden";
    private const string ObsoleteOptionsTypeName = "Rag.NET.Graph.Algorithms.LeidenOptions";

    /// <summary>The obsolete C# diagnostic: a use of an <c>[Obsolete]</c> symbol that is not an error.</summary>
    private const string DeprecationWarningId = "CS0618";

    /// <summary>A consumer written against the old names, as one would have been on 0.1.0.</summary>
    private const string ConsumerSource = """
        using System.Collections.Generic;
        using Rag.NET.Graph;
        using Rag.NET.Graph.Algorithms;

        public static class OldConsumer
        {
            public static IReadOnlyList<Community> Cluster(GraphSnapshot graph) =>
                Leiden.Detect(graph, new LeidenOptions { Resolution = 1.5, RandomSeed = 3 });
        }
        """;

    [Fact]
    public void TheForwarderReturnsTheSamePartitionAsTheTypeItForwardsTo()
    {
        var graph = BuildTwoCliquesJoinedByABridge();

        var throughForwarder = InvokeObsoleteDetect(graph, resolution: 1.0, randomSeed: 42);
        var direct = LouvainWithRefinement.Detect(
            graph, new LouvainWithRefinementOptions { Resolution = 1.0, RandomSeed = 42 });

        Assert.Equal(Memberships(direct), Memberships(throughForwarder));
    }

    [Fact]
    public void TheForwarderCarriesTheOldOptionsObjectsValuesThrough()
    {
        // The point of LeidenOptions deriving from the type that replaced it: a caller's settings
        // must still reach the algorithm. A forwarder that dropped them would agree with the new
        // type on the defaults and disagree here, which is why the resolution is not the default.
        var graph = BuildTwoCliquesJoinedByABridge();

        var throughForwarder = InvokeObsoleteDetect(graph, resolution: 4.0, randomSeed: 7);
        var direct = LouvainWithRefinement.Detect(
            graph, new LouvainWithRefinementOptions { Resolution = 4.0, RandomSeed = 7 });

        Assert.Equal(Memberships(direct), Memberships(throughForwarder));
        Assert.NotEqual(
            Memberships(LouvainWithRefinement.Detect(graph)),
            Memberships(throughForwarder));
    }

    [Theory]
    [InlineData(ObsoleteClustererTypeName, "LouvainWithRefinement")]
    [InlineData(ObsoleteOptionsTypeName, "LouvainWithRefinementOptions")]
    public void EveryObsoleteNameSaysWhatToUseAndWhyTheOldNameWasWrong(string typeName, string replacement)
    {
        var attribute = ObsoleteType(typeName).GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(attribute);
        Assert.False(
            attribute.IsError,
            $"{typeName} is marked obsolete as an error, which breaks every caller on 0.1.0 " +
            "outright — the deprecation path is the whole point of keeping the name.");

        var message = attribute.Message ?? string.Empty;
        Assert.Contains(replacement, message, StringComparison.Ordinal);
        Assert.Contains("not Traag/Waltman/van Eck's Leiden algorithm", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConsumerOfTheOldNamesGetsAWarningAndNotAnError()
    {
        var diagnostics = CompileConsumer();

        Assert.DoesNotContain(
            diagnostics,
            d => d.Severity == DiagnosticSeverity.Error);

        var deprecations = diagnostics
            .Where(d => string.Equals(d.Id, DeprecationWarningId, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(deprecations);
        Assert.All(deprecations, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains(
            deprecations,
            d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                .Contains("LouvainWithRefinement", StringComparison.Ordinal));
    }

    /// <summary>Compiles <see cref="ConsumerSource"/> against the built Rag.NET.Graph assembly.</summary>
    /// <returns>Every diagnostic the compiler produced.</returns>
    private static IReadOnlyList<Diagnostic> CompileConsumer()
    {
        var compilation = CSharpCompilation.Create(
            "ObsoleteLeidenConsumer",
            [CSharpSyntaxTree.ParseText(ConsumerSource)],
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics();
    }

    /// <summary>
    /// Every assembly loaded beside the test binary, which is where Rag.NET.Graph and the shared
    /// framework's reference set both land.
    /// </summary>
    /// <returns>Metadata references for the consumer compilation.</returns>
    private static IReadOnlyList<MetadataReference> ReferenceAssemblies()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
        };

        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            foreach (var file in Directory.GetFiles(directory, "*.dll"))
            {
                if (seen.Add(Path.GetFileName(file)) && IsManaged(file))
                {
                    references.Add(MetadataReference.CreateFromFile(file));
                }
            }
        }

        return references;
    }

    /// <summary>Whether a file on disk is an assembly Roslyn can read as a reference.</summary>
    /// <param name="path">The candidate .dll.</param>
    /// <returns>Whether it carries metadata.</returns>
    private static bool IsManaged(string path)
    {
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Calls <c>Leiden.Detect</c> with a <c>LeidenOptions</c>, both by reflection.</summary>
    /// <param name="graph">The graph to cluster.</param>
    /// <param name="resolution">The resolution to set on the obsolete options object.</param>
    /// <param name="randomSeed">The seed to set on the obsolete options object.</param>
    /// <returns>Whatever the forwarder returned.</returns>
    private static IReadOnlyList<Community> InvokeObsoleteDetect(
        GraphSnapshot graph, double resolution, int randomSeed)
    {
        var optionsType = ObsoleteType(ObsoleteOptionsTypeName);
        var options = Activator.CreateInstance(optionsType);
        optionsType.GetProperty("Resolution")!.SetValue(options, resolution);
        optionsType.GetProperty("RandomSeed")!.SetValue(options, randomSeed);

        var detect = ObsoleteType(ObsoleteClustererTypeName)
            .GetMethod("Detect", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(detect);
        return (IReadOnlyList<Community>)detect.Invoke(null, [graph, options])!;
    }

    /// <summary>Resolves a deprecated type by name, so no source file has to name it.</summary>
    /// <param name="typeName">The full name of the obsolete type.</param>
    /// <returns>The type.</returns>
    private static Type ObsoleteType(string typeName) =>
        typeof(LouvainWithRefinement).Assembly.GetType(typeName, throwOnError: true)!;

    /// <summary>The partition, as each entity's sorted set of community-mates.</summary>
    /// <param name="communities">The detected communities.</param>
    /// <returns>A comparable rendering of the partition, independent of community numbering.</returns>
    private static IReadOnlyList<string> Memberships(IReadOnlyList<Community> communities) =>
        [.. communities
            .Select(c => string.Join(',', c.MemberEntities.Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)];

    /// <summary>Two four-node cliques joined by one edge — a partition nobody disputes.</summary>
    /// <returns>The graph.</returns>
    private static GraphSnapshot BuildTwoCliquesJoinedByABridge()
    {
        var entities = new List<GraphEntity>();
        var relationships = new List<GraphRelationship>();

        for (int clique = 0; clique < 2; clique++)
        {
            for (int i = 0; i < 4; i++)
            {
                entities.Add(new GraphEntity($"c{clique}n{i}", "Node", $"Node {i}"));
            }

            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    relationships.Add(
                        new GraphRelationship($"c{clique}n{i}", $"c{clique}n{j}", "linked"));
                }
            }
        }

        relationships.Add(new GraphRelationship("c0n0", "c1n0", "bridge"));

        return new GraphSnapshot(entities, relationships, []);
    }
}
