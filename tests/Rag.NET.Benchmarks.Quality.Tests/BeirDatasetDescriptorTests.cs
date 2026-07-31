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
        // twice found numbers whose origin nobody could reconstruct. SciFact's own note is that no
        // source was recorded at the time — an acknowledged gap rather than an invented citation.
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
    public void All_ListsEveryDescribedDataset_SoADescriptorCannotExistWithoutBeingMeasured()
    {
        // The parity theory enumerates All. A dataset described but left out of this list would be
        // a descriptor nothing ever runs — which reads, from the test summary, exactly like a
        // dataset that passed.
        Assert.Contains(BeirDatasetDescriptor.SciFact, BeirDatasetDescriptor.All);

        foreach (var descriptor in BeirDatasetDescriptor.All)
        {
            Assert.NotNull(descriptor.ParityTarget);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ParityTarget.Source));
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
