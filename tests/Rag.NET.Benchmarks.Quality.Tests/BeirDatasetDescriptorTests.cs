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
