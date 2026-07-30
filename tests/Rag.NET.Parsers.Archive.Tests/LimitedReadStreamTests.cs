using System.IO.Compression;
using Xunit;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// The bombs, written before any parser exists, because this is where the phase's security value
/// lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case asserts which bound was reported, not merely that something threw.</b> A bomb that
/// trips the wrong cap is a test passing for the wrong reason: a low-ratio archive stopped by the
/// ratio check would mean the ratio arithmetic is wrong, and a green suite would say the opposite.
/// </para>
/// <para>
/// Each bound was mutation-checked by deleting its refusal from <see cref="LimitedReadStream"/> and
/// confirming the cases below went red — the total-bytes cases with nothing thrown at all, the ratio
/// case likewise.
/// </para>
/// </remarks>
public class LimitedReadStreamTests
{
    /// <summary>A megabyte, the one fixture deliberately not kept tiny. See <see cref="ZipFixtureBuilder"/>.</summary>
    private const int OneMegabyte = 1024 * 1024;

    // ── The fixture itself ───────────────────────────────────────────────────

    [Fact]
    public void TheZeroFixtureIsGenuinelyAHighRatioEntry()
    {
        // The ratio bomb below is only a bomb if this holds. Asserted rather than assumed, so a
        // runtime change to the deflate implementation shows up here as a fixture that stopped being
        // a bomb, rather than as a bomb test that silently stopped testing bombs.
        using var source = new MemoryStream(ZipFixtureBuilder.Archive(("zeros.bin", ZipFixtureBuilder.Zeros(OneMegabyte))));
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        var entry = archive.Entries[0];

        Assert.Equal(OneMegabyte, entry.Length);
        Assert.True(
            entry.CompressedLength < 8 * 1024,
            $"A megabyte of zero bytes compressed to {entry.CompressedLength} bytes, which is not the " +
            "roughly-a-kilobyte this fixture depends on.");
        Assert.True(
            entry.Length / entry.CompressedLength > 250,
            $"The zero fixture's real ratio is {entry.Length / entry.CompressedLength}:1, too low to " +
            "prove ratio detection against real compressed data.");
    }

    // ── One bomb per bound ───────────────────────────────────────────────────

    [Fact]
    public async Task ATotalBytesBombIsReportedAsTheTotalBytesCap()
    {
        var archive = ZipFixtureBuilder.Archive(("payload.bin", ZipFixtureBuilder.Incompressible(300, seed: 1)));
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = 100,
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio,
        };

        var exception = await AssertRefusedAsync(archive, options);

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.Equal(100, exception.Allowed);
    }

    [Fact]
    public async Task APerEntryRatioBombIsReportedAsTheRatioCap()
    {
        // A real megabyte of zeros at a real ~1000:1, read against a lowered ratio bound. The total
        // bound is set far out of reach so it cannot be what fires.
        var archive = ZipFixtureBuilder.Archive(("zeros.bin", ZipFixtureBuilder.Zeros(OneMegabyte)));
        var options = new ArchiveParserOptions
        {
            MaxCompressionRatio = 100,
            MaxTotalUncompressedBytes = 10 * OneMegabyte,
        };

        var exception = await AssertRefusedAsync(archive, options);

        Assert.Equal(ArchiveLimit.CompressionRatio, exception.Limit);
        Assert.Equal(100, exception.Allowed);
    }

    [Fact]
    public async Task ALowRatioHighTotalArchiveIsReportedAsTheTotalBytesCap()
    {
        // The case two bounds would miss, and the reason there are three. Four entries of
        // incompressible bytes: no entry expands at all, so the ratio bound has nothing to object to,
        // and four entries is nowhere near the entry bound — yet together they exceed the archive's
        // byte budget.
        //
        // It is also the test that catches a per-entry byte counter. No single entry here passes
        // 100,000 bytes; only the shared total does.
        var archive = ZipFixtureBuilder.Archive(
            ("a.bin", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 1)),
            ("b.bin", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 2)),
            ("c.bin", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 3)),
            ("d.bin", ZipFixtureBuilder.Incompressible(32 * 1024, seed: 4)));
        var options = new ArchiveParserOptions { MaxTotalUncompressedBytes = 100_000, MaxCompressionRatio = 100 };

        AssertEveryEntryIsInnocentOfTheOtherTwoBounds(archive, options);

        var exception = await AssertRefusedAsync(archive, options);

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.Equal(100_000, exception.Allowed);
    }

    // ── Reads that stay under every bound ────────────────────────────────────

    [Fact]
    public async Task ReadsUnderEveryCapPassThroughByteForByte()
    {
        var first = ZipFixtureBuilder.Incompressible(4_000, seed: 7);
        var second = ZipFixtureBuilder.Zeros(4_000);
        var archive = ZipFixtureBuilder.Archive(("first.bin", first), ("second.bin", second));

        // The zero entry expands about 400:1 on its own, so the ratio bound has to be permissive for
        // this to be a pass-through test rather than a second ratio bomb.
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = 1_000_000,
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio,
        };

        var contents = await ReadEveryEntryAsync(archive, options);

        Assert.Equal(2, contents.Count);
        Assert.Equal(first, contents[0]);
        Assert.Equal(second, contents[1]);
    }

    [Fact]
    public async Task AnArchiveExactlyAtTheTotalCapIsAdmitted()
    {
        // Pins the comparison as "more than the bound" rather than "as much as the bound". Reading
        // exactly the budget is the honest archive's best case, and refusing it would make the
        // configured number mean one byte less than it says.
        var archive = ZipFixtureBuilder.Archive(("payload.bin", ZipFixtureBuilder.Incompressible(300, seed: 11)));
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = 300,
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio,
        };

        Assert.Equal(300, await DrainAsync(archive, options));
    }

    // ── The boundary, and what must not be caught ────────────────────────────

    [Fact]
    public async Task ACapFallingInsideABufferIsCaughtOnThatRead()
    {
        // The bound is 100 and reads are 64 bytes, so the second read spans 64…128 and the bound
        // falls in the middle of it. A check written at buffer granularity — "would this whole read
        // fit?" — either refuses the archive 36 bytes early or lets it through to 128, and the
        // observed count says which happened.
        var archive = ZipFixtureBuilder.Archive(("payload.bin", ZipFixtureBuilder.Incompressible(300, seed: 13)));
        var options = new ArchiveParserOptions
        {
            MaxTotalUncompressedBytes = 100,
            MaxCompressionRatio = ArchiveParserOptions.MaxSupportedCompressionRatio,
        };

        var exception = await AssertRefusedAsync(archive, options, bufferSize: 64);

        Assert.Equal(ArchiveLimit.TotalUncompressedBytes, exception.Limit);
        Assert.Equal(128, exception.Observed);
    }

    [Fact]
    public async Task ACancelledUnderlyingReadIsNotSwallowed()
    {
        // Cancellation is the caller's decision about the whole parse, not evidence that this entry
        // is a bomb. A stream that caught broadly to re-raise its own exception would turn a
        // cancelled ingestion into a bomb report, and the caller would go looking for an attacker.
        var budget = new ArchiveReadBudget(new ArchiveParserOptions());
        await using var limited = new LimitedReadStream(new CancellingStream(), compressedLength: 1000, budget);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var read = await limited.ReadAsync(new byte[64], TestContext.Current.CancellationToken);
            Assert.Fail($"Expected the cancellation to propagate, but the read returned {read} bytes.");
        });
    }

    [Fact]
    public async Task AnEntryDeclaringNoCompressedLengthIsTreatedAsBombGradeExpansion()
    {
        // The defensive reading of a nonsensical header. Zero compressed bytes producing output at
        // all is an infinite ratio; dividing by it would be the alternative, and a crash is not a
        // refusal.
        var budget = new ArchiveReadBudget(new ArchiveParserOptions { MaxCompressionRatio = 1 });
        await using var limited = new LimitedReadStream(new MemoryStream(new byte[64]), compressedLength: 0, budget);

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(async () =>
        {
            var read = await limited.ReadAsync(new byte[64], TestContext.Current.CancellationToken);
            Assert.Fail($"Expected a refusal, but the read returned {read} bytes.");
        });

        Assert.Equal(ArchiveLimit.CompressionRatio, exception.Limit);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that neither the ratio bound nor the entry bound could have caught this archive, so
    /// the refusal that follows can only be the total-bytes one.
    /// </summary>
    private static void AssertEveryEntryIsInnocentOfTheOtherTwoBounds(byte[] archiveBytes, ArchiveParserOptions options)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);

        Assert.True(
            archive.Entries.Count < options.MaxEntries,
            $"{archive.Entries.Count} entries is not below the entry bound of {options.MaxEntries}, so " +
            "this archive is not the low-ratio, high-total case it claims to be.");

        foreach (var entry in archive.Entries)
        {
            Assert.True(
                entry.Length <= entry.CompressedLength * (long)options.MaxCompressionRatio,
                $"Entry '{entry.FullName}' expands {entry.Length}:{entry.CompressedLength}, which the " +
                $"ratio bound of {options.MaxCompressionRatio} would have caught on its own.");
        }
    }

    private static async Task<ArchiveLimitExceededException> AssertRefusedAsync(
        byte[] archiveBytes,
        ArchiveParserOptions options,
        int bufferSize = 4096) =>
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(
            async () => await DrainAsync(archiveBytes, options, bufferSize));

    /// <summary>Reads every entry to exhaustion through the limiting stream, returning the byte count.</summary>
    private static async Task<long> DrainAsync(byte[] archiveBytes, ArchiveParserOptions options, int bufferSize = 4096)
    {
        var contents = await ReadEveryEntryAsync(archiveBytes, options, bufferSize);
        long total = 0;

        for (var index = 0; index < contents.Count; index++)
        {
            total += contents[index].Length;
        }

        return total;
    }

    /// <summary>
    /// Reads every entry through one shared <see cref="ArchiveReadBudget"/>, which is how the parser
    /// will read them: one budget per archive, one limiting stream per entry.
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>> ReadEveryEntryAsync(
        byte[] archiveBytes,
        ArchiveParserOptions options,
        int bufferSize = 4096)
    {
        var budget = new ArchiveReadBudget(options);
        var contents = new List<byte[]>();
        var buffer = new byte[bufferSize];

        using var source = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            using var collected = new MemoryStream();
            await using (var limited = new LimitedReadStream(entry.Open(), entry.CompressedLength, budget))
            {
                int read;
                while ((read = await limited.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken)) > 0)
                {
                    collected.Write(buffer, 0, read);
                }
            }

            contents.Add(collected.ToArray());
        }

        return contents;
    }

    /// <summary>A stream whose reads report cancellation, however they were called.</summary>
    private sealed class CancellingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
