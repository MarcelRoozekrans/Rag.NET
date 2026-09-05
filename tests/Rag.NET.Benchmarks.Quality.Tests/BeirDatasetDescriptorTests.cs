using System.Globalization;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins what a <see cref="BeirDatasetDescriptor"/> carries: the published checksum, the counts that
/// are actually on disk, the licence, and — since the parity test became a theory over datasets —
/// the parity target and band that used to be three constants in a test file.
/// <para>
/// These are the values a parity run is judged against, so they are asserted here where a wrong one
/// fails a pull request, rather than only inside the nightly measurement where a wrong one would
/// simply move the band under the number.
/// </para>
/// </summary>
public sealed class BeirDatasetDescriptorTests
{
    [Fact]
    public void SciFact_RecordsItsLicence()
    {
        // BEIR publishes no per-dataset licence — its README says only that it "downloaded and
        // prepared public datasets" and that permission remains the user's responsibility — so the
        // licence has to be recorded on our side or it is recorded nowhere.
        var licence = BeirDatasetDescriptor.SciFact.Licence;

        Assert.Contains("ODC-By 1.0", licence, StringComparison.Ordinal);
        Assert.Contains("CC BY 4.0", licence, StringComparison.Ordinal);
        Assert.Contains("github.com/allenai/scifact", licence, StringComparison.Ordinal);
    }

    [Fact]
    public void SciFact_CarriesThePublishedChecksumAndTheCountsOnDisk()
    {
        var scifact = BeirDatasetDescriptor.SciFact;

        Assert.Equal("5f7d1de60b170fc8027bb7898e2efca1", scifact.ArchiveMd5, StringComparer.Ordinal);
        Assert.Equal(5183, scifact.DocumentCount);
        Assert.Equal(1109, scifact.QueryCount);
        Assert.Equal(300, scifact.TestQueryCount);
        Assert.Equal("scifact.zip", scifact.ArchiveFileName, StringComparer.Ordinal);
    }

    [Fact]
    public void SciFact_CarriesTheParityTargetAndBandThatUsedToBeHardCodedInTheTest()
    {
        // The exact three numbers SciFactParityTests declared before the target moved onto the
        // descriptor. Moving them was meant to change nothing about what the run asserts, and this
        // is the assertion that says so.
        var target = BeirDatasetDescriptor.SciFact.ParityTarget;

        Assert.Equal(0.645, target.PublishedNdcgAt10, 10);
        Assert.Equal(0.625, target.LowerBound, 10);
        Assert.Equal(0.665, target.UpperBound, 10);
        Assert.Equal(BeirParityTarget.DefaultTolerance, target.Tolerance, 10);
    }

    [Fact]
    public void ParityTarget_RecordsWhereItsPublishedFigureCameFrom()
    {
        // A figure without a provenance is a figure nobody can re-check, and this milestone has
        // twice found numbers whose origin nobody could reconstruct. SciFact's was one of them until
        // Phase 3.12 found the figure it had been carrying — see
        // SciFact_NowCitesTheFigureItCarriedUnsourcedForTwoPhases.
        var source = BeirDatasetDescriptor.SciFact.ParityTarget.Source;

        Assert.False(string.IsNullOrWhiteSpace(source));
        Assert.Contains("all-MiniLM-L6-v2", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.625, true)]
    [InlineData(0.645, true)]
    [InlineData(0.665, true)]
    [InlineData(0.62, false)]
    [InlineData(0.7, false)]
    public void ParityTarget_BandIsTwoSidedAndInclusiveAtBothEdges(double ndcg, bool expected)
    {
        // Two-sided on purpose: scoring materially ABOVE a model's own published figure indicates a
        // leak, so an upper bound that let anything through would be the more dangerous of the two
        // to lose.
        var target = new BeirParityTarget(0.645, "test");

        Assert.Equal(expected, target.Contains(ndcg));
    }

    [Fact]
    public void SciFact_NowCitesTheFigureItCarriedUnsourcedForTwoPhases()
    {
        // Looking FiQA's and ArguAna's figures up found SciFact's as a by-product: MTEB reports
        // 0.64508 for this model on SciFact's test split, which is the 0.645 Phase 3.7 measured
        // against and never cited. The target stays 0.645 — moving it would change what the phase's
        // regression gate asserts, for 0.001.
        var source = BeirDatasetDescriptor.SciFact.ParityTarget.Source;

        Assert.Equal(0.645, BeirDatasetDescriptor.SciFact.ParityTarget.PublishedNdcgAt10, 10);
        Assert.Contains("0.64508", source, StringComparison.Ordinal);
        Assert.Contains("embeddings-benchmark/results", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FiQA_CarriesThePublishedChecksumAndTheCountsOnDisk()
    {
        // MD5 from BEIR's README table; counts from the downloaded archive, where 1,706 judgements
        // fall over 648 distinct query ids — 2.6327 per query, which is what MTEB's FiQA2018
        // metadata records too.
        var fiqa = BeirDatasetDescriptor.FiQA;

        Assert.Equal("17918ed23cd04fb15047f73e6c3bd9d9", fiqa.ArchiveMd5, StringComparer.Ordinal);
        Assert.Equal(57638, fiqa.DocumentCount);
        Assert.Equal(6648, fiqa.QueryCount);
        Assert.Equal(648, fiqa.TestQueryCount);
        Assert.Equal("fiqa.zip", fiqa.ArchiveFileName, StringComparer.Ordinal);
    }

    [Fact]
    public void FiQA_CarriesTheParityTargetAndBandItsOwnSourceStringQuotes()
    {
        // Pinned for the same reason SciFact's is, and more urgently. SciFact's target was pinned
        // from the day it moved onto the descriptor; FiQA's and ArguAna's arrived in Phase 3.12
        // pinned by nothing, and the gap was demonstrated rather than argued: changing this target
        // from 0.36867 to 0.40000 — leaving FiQAPublishedSource, which literally begins "0.36867
        // for all-MiniLM-L6-v2", untouched — passed all 107 tests in this project.
        //
        // The exposure is not symmetric, and FiQA has the worse half of it. A wrong ArguAna target
        // is still caught by the nightly, whose ~50 s warm parity leg runs unasked. A wrong FiQA
        // target is caught only by a case BeirRunBudget gates behind RAGNET_BEIR_LONG_RUNS at
        // 1 h 11 m, which nothing sets — so in practice the descriptor is FiQA's only guard, and a
        // target that silently disagreed with the provenance string quoting it would move the band
        // under the number with nothing to say so.
        var target = BeirDatasetDescriptor.FiQA.ParityTarget;

        Assert.Equal(0.36867, target.PublishedNdcgAt10, 10);
        Assert.Equal(0.34867, target.LowerBound, 10);
        Assert.Equal(0.38867, target.UpperBound, 10);
        Assert.Equal(BeirParityTarget.DefaultTolerance, target.Tolerance, 10);
    }

    [Fact]
    public void ArguAna_CarriesTheParityTargetAndBandItsOwnSourceStringQuotes()
    {
        // ArguAna's parity leg does run nightly, so a wrong target here fails within a day rather
        // than never. That is a reason to pin it as well as FiQA's, not instead: "the nightly will
        // notice" is a guard measured in hours against a pull request measured in seconds, and it
        // is the guard that reports a failed measurement rather than a wrong constant.
        var target = BeirDatasetDescriptor.ArguAna.ParityTarget;

        Assert.Equal(0.50167, target.PublishedNdcgAt10, 10);
        Assert.Equal(0.48167, target.LowerBound, 10);
        Assert.Equal(0.52167, target.UpperBound, 10);
        Assert.Equal(BeirParityTarget.DefaultTolerance, target.Tolerance, 10);
    }

    [Fact]
    public void EveryPublishedFigureAppearsVerbatimInTheSourceStringThatCitesIt()
    {
        // BeirParityTarget's own documentation says a number and its provenance "cannot drift
        // apart" because they live in one record, and retrieval-quality.md repeats the claim. That
        // was architecture, not enforcement: the record holds a double and a string, and nothing
        // compared them. This is the comparison, and it generalises — a fourth dataset gets it for
        // free, which the two assertions above deliberately do not.
        //
        // "0.#####" rather than F5, because each source string quotes its figure the way it is
        // written upstream: SciFact's opens "0.645", not "0.64500".
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            var target = descriptor.ParityTarget;
            var figure = target.PublishedNdcgAt10.ToString("0.#####", CultureInfo.InvariantCulture);

            Assert.Contains(
                figure,
                target.Source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FiQA_HasNoTitlesAtAll_WhichIsWhyOnlyOneSeparatorIsWorthMeasuring()
    {
        // Every one of the 57,638 corpus lines has an empty title, so title + sep + text trims to
        // the same bytes whatever the separator is. BeirParityTests reads this to decide whether the
        // newline case is worth an hour of CPU.
        Assert.Equal(0, BeirDatasetDescriptor.FiQA.TitledDocumentCount);
    }

    [Fact]
    public void FiQA_RecordsThatUpstreamNamesNoLicenceAndForbidsCommercialUse()
    {
        // The sharpest disagreement in the file: upstream restricts to non-commercial use and names
        // no licence; BeIR/fiqa declares cc-by-sa-4.0, which permits exactly what upstream refuses.
        var licence = BeirDatasetDescriptor.FiQA.Licence;

        Assert.Contains("non-commercial use", licence, StringComparison.Ordinal);
        Assert.Contains("sites.google.com/view/fiqa", licence, StringComparison.Ordinal);
        Assert.Contains("cc-by-sa-4.0", licence, StringComparison.Ordinal);
    }

    [Fact]
    public void ArguAna_CarriesThePublishedChecksumAndTheCountsOnDisk()
    {
        // Every query judged, exactly one relevant document each: 1,406 rows over 1,406 query ids.
        var arguana = BeirDatasetDescriptor.ArguAna;

        Assert.Equal("8ad3e3c2a5867cdced806d6503f29b99", arguana.ArchiveMd5, StringComparer.Ordinal);
        Assert.Equal(8674, arguana.DocumentCount);
        Assert.Equal(1406, arguana.QueryCount);
        Assert.Equal(1406, arguana.TestQueryCount);
        Assert.Equal(2699, arguana.TitledDocumentCount);
        Assert.Equal("arguana.zip", arguana.ArchiveFileName, StringComparer.Ordinal);
    }

    [Fact]
    public void ArguAna_RecordsTheUpstreamLicenceAndTheMirrorsThatDisagreeWithIt()
    {
        // Upstream is the Zenodo deposit, because BEIR's linked homepage is dead. Both mirrors add a
        // share-alike obligation upstream does not impose; recorded, not resolved.
        var licence = BeirDatasetDescriptor.ArguAna.Licence;

        Assert.Contains("CC BY 4.0", licence, StringComparison.Ordinal);
        Assert.Contains("zenodo.3973258", licence, StringComparison.Ordinal);
        Assert.Contains("cc-by-sa-4.0", licence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scifact", false)]
    [InlineData("fiqa", true)]
    [InlineData("arguana", true)]
    public void ExcludesSelfRetrievedDocument_MatchesMtebsIgnoreIdenticalIds(string name, bool expected)
    {
        // mteb's ArguAnaRetrieval and FiQA2018Retrieval both set ignore_identical_ids = True;
        // SciFactRetrieval leaves AbsTaskRetrieval's False. These are the runs the published figures
        // in ParityTarget came from, so the flag is part of the figure and not a preference.
        //
        // On ArguAna it is the difference between a runnable dataset and an unrunnable one: 1,298 of
        // its 1,406 queries are byte-identical to the corpus document sharing their id.
        Assert.Equal(expected, BeirDatasetDescriptor.ByName(name).ExcludesSelfRetrievedDocument);
    }

    [Fact]
    public void EveryPublishedFigureNamesItsSourceAndTheModelItIsFor()
    {
        // The figure is per model, and a figure without a provenance is a figure nobody can
        // re-check. MTEB's results repository is cited by model revision rather than by leaderboard
        // screenshot, so the citation survives the leaderboard being re-rendered.
        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            var source = descriptor.ParityTarget.Source;

            Assert.Contains("all-MiniLM-L6-v2", source, StringComparison.Ordinal);
            Assert.Contains("8b3219a92973c328a8e22fadcfa821b5dc75636a", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void All_ListsEveryDescribedDataset_SoADescriptorCannotExistWithoutBeingMeasured()
    {
        // The parity theory enumerates All. A dataset described but left out of this list would be
        // a descriptor nothing ever runs — which reads, from the test summary, exactly like a
        // dataset that passed.
        Assert.Contains(BeirDatasetDescriptor.SciFact, BeirDatasetDescriptor.All);
        Assert.Contains(BeirDatasetDescriptor.FiQA, BeirDatasetDescriptor.All);
        Assert.Contains(BeirDatasetDescriptor.ArguAna, BeirDatasetDescriptor.All);

        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            Assert.NotNull(descriptor.ParityTarget);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ParityTarget.Source));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Licence));
            Assert.True(descriptor.TestQueryCount <= descriptor.QueryCount);
            Assert.True(descriptor.TitledDocumentCount <= descriptor.DocumentCount);
        }
    }

    [Fact]
    public void TheFourBeirDatasetsSupportEveryProtocolExceptTheGraphPair_SoNoMeasuredCellIsGatedOff()
    {
        // Originally EveryExistingDatasetSupportsEveryProtocol_SoThisChangeMovesNothing, over
        // BeirDatasetDescriptor.All and every protocol. Landing MultiHop-RAG made that false twice
        // over, both times by design: All now holds a fifth descriptor that declares nine protocols
        // inapplicable, and BeirProtocol gained an eleventh member — GraphRag — that the four BEIR
        // datasets cannot be judged under, so they now name the ten they can. Phase 5.2.1 added a
        // twelfth, GraphRagDepthControl, which is excluded with the protocol it controls for.
        //
        // The scope narrowed rather than the assertion weakening, because the job has not changed.
        // What this catches is somebody quietly restricting one of the four datasets whose cells are
        // already measured and pinned in BeirRunBudget and BeirReproduction: a restriction there
        // gates off a measured cell, and a gated-off cell reads from a test summary exactly like a
        // cell that passed. Naming the four and the ten is what keeps that catchable while letting
        // a fifth dataset — and the graph pair — exist. Weakening it to "All, where applicable"
        // would have been the same test asserting nothing, since it would agree with whatever the
        // descriptors happen to say.
        BeirDatasetDescriptor[] beirDatasets =
            [
                BeirDatasetDescriptor.SciFact,
                BeirDatasetDescriptor.FiQA,
                BeirDatasetDescriptor.ArguAna,
                BeirDatasetDescriptor.TrecCovid,
            ];

        foreach (var descriptor in beirDatasets)
        {
            foreach (var protocol in Enum.GetValues<BeirProtocol>())
            {
                AssertApplicability(descriptor, protocol);
            }
        }
    }

    /// <summary>Asserts one dataset/protocol pair is applicable exactly where it should be.</summary>
    /// <remarks>
    /// Extracted from the loop above only because the method outgrew the length analyser. Each
    /// branch states why the pair is what it is, because "descriptor does not support protocol" is
    /// indistinguishable in a test summary from "cell passed".
    /// </remarks>
    private static void AssertApplicability(BeirDatasetDescriptor descriptor, BeirProtocol protocol)
    {
        if (protocol is BeirProtocol.GraphRag or BeirProtocol.GraphRagDepthControl)
        {
            Assert.False(
                descriptor.Supports(protocol),
                $"{descriptor.Name} started supporting {protocol}. The graph protocol and its " +
                "depth control are MultiHop-RAG's; a BEIR dataset claiming either would owe a " +
                "budget cell and a reproduction entry for a run whose judgements cannot reward a " +
                "graph, or for a control with no graph run to control for.");
            return;
        }

        if (protocol is BeirProtocol.RealTagFiltered or BeirProtocol.RealSelfQuery)
        {
            // SciFact's alone, and asserted in BOTH directions rather than exempted. The protocol
            // names a store composition -- SciFact retrieved out of a SciFact+FiQA store -- rather
            // than a technique applied to a corpus, so "FiQA under RealTagFiltered" would be a
            // different pairing with a different control: a run that does not exist rather than one
            // nobody has got to. Exempting it the way the graph pair is exempted would let somebody
            // turn it ON for a dataset owing no budget cell and no reproduction entry, which is the
            // same hole this test exists to close from the other side.
            var isSciFact = string.Equals(
                descriptor.Name, BeirDatasetDescriptor.SciFact.Name, StringComparison.Ordinal);

            Assert.True(
                descriptor.Supports(protocol) == isSciFact,
                isSciFact
                    ? $"{descriptor.Name} stopped supporting {protocol}, gating off a measured cell."
                    : $"{descriptor.Name} started supporting {protocol}, but that protocol is " +
                      "SciFact's pairing and this dataset owes no budget cell or reproduction " +
                      "entry for it.");
            return;
        }

        Assert.True(
            descriptor.Supports(protocol),
            $"{descriptor.Name} stopped supporting {protocol}, which would gate off a measured cell.");
    }

    [Fact]
    public void ADescriptorThatRestrictsItsProtocols_SupportsThoseAndOnlyThose()
    {
        // The test above cannot reach this branch. Every existing descriptor leaves
        // ApplicableProtocols null, so "is null" short-circuits on all 40 of its iterations and
        // Contains is never called — the half of Supports that actually gates anything would have
        // shipped with zero coverage. This repository's recurring defect is guards that report
        // green while covering nothing, so the restricted path gets its own case.
        var restricted = BeirDatasetDescriptor.SciFact with
        {
            ApplicableProtocols = BeirProtocolSet.Of(BeirProtocol.Parity),
        };

        Assert.True(restricted.Supports(BeirProtocol.Parity));
        Assert.False(restricted.Supports(BeirProtocol.Real));
    }

    [Fact]
    public void ADescriptorCannotDeclareAnEmptySetOfProtocols_BecauseNothingWouldEverRunIt()
    {
        // An empty set makes Supports answer false for everything, which is a descriptor that is
        // described and then measured by nothing. All's own documentation says that state cannot
        // arise and All_ListsEveryDescribedDataset exists to prevent it, but neither can see it:
        // both check that a descriptor is listed, not that anything can run it. So the refusal has
        // to happen at construction, where it names the mistake, rather than hours later as a
        // universal skip.
        var exception = Assert.Throws<ArgumentException>(
            () => BeirDatasetDescriptor.SciFact with
            {
                ApplicableProtocols = BeirProtocolSet.Of(),
            });

        Assert.Contains("empty set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestrictedDescriptorIgnoresLaterMutationOfTheSetItWasGiven()
    {
        // This can no longer fail on its own: BeirProtocolSet is a struct over a bitmask, so there
        // is nothing for the caller below to still be holding. That is exactly why it stays. It was
        // written when the property took an IReadOnlySet<T> — a read-only view, not an immutable
        // set — where a caller keeping its HashSet could change what a static descriptor supports
        // at runtime, process-wide, from whichever test happened to run first. It is now the
        // tripwire for the storage type rather than for the accessor: swap in anything that is a
        // view onto somebody else's collection and this goes red again.
        var mutable = new HashSet<BeirProtocol> { BeirProtocol.Parity };
        var restricted = BeirDatasetDescriptor.SciFact with
        {
            ApplicableProtocols = BeirProtocolSet.Of([.. mutable]),
        };

        mutable.Add(BeirProtocol.Real);

        Assert.False(restricted.Supports(BeirProtocol.Real));
    }

    [Fact]
    public void TwoDescriptorsWithTheSameProtocols_AreEqual()
    {
        // A record compares its fields with EqualityComparer<T>.Default, and no BCL set overrides
        // Equals — not HashSet, not FrozenSet, not ImmutableHashSet — so this used to be reference
        // equality: two descriptors printing character-for-character identically compared unequal
        // and hashed differently. Deferred while only one descriptor restricted its protocols, and
        // fixed before a second could inherit the surprise.
        //
        // The two sets are built separately and in opposite orders, so what is asserted is that the
        // protocols are compared, not that an instance is shared.
        var a = BeirDatasetDescriptor.SciFact with
        {
            ApplicableProtocols = BeirProtocolSet.Of(BeirProtocol.Parity, BeirProtocol.Real),
        };

        var b = BeirDatasetDescriptor.SciFact with
        {
            ApplicableProtocols = BeirProtocolSet.Of(BeirProtocol.Real, BeirProtocol.Parity),
        };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ByName_FindsADescribedDataset()
    {
        Assert.Same(BeirDatasetDescriptor.SciFact, BeirDatasetDescriptor.ByName("scifact"));
    }

    [Fact]
    public void ByName_RejectsAnUnknownName_RatherThanReturningNull()
    {
        // A null here would reach the parity run as a NullReferenceException several minutes and one
        // corpus download later.
        var exception = Assert.Throws<ArgumentException>(
            () => BeirDatasetDescriptor.ByName("SciFact"));

        Assert.Contains("SciFact", exception.Message, StringComparison.Ordinal);
    }
}
