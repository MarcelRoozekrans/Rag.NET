using System.IO.Compression;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// Builds real, in-memory ZIP archives for the read-limiting tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is 256 MB.</b> A 1 KB archive read against
/// <c>MaxTotalUncompressedBytes = 100</c> drives exactly the code path a 300 MB archive drives
/// against the default, and does it in microseconds. The bounds are lowered in the test; the fixtures
/// stay small.
/// </para>
/// <para>
/// <b>One deliberate exception:</b> <see cref="Zeros"/> at a megabyte. Compression ratio is the one
/// property a small fixture cannot fake — a counter told to pretend it saw a 1000:1 entry proves the
/// counter, not the detection — so the ratio bomb is built from a megabyte of zero bytes, which
/// deflate genuinely takes to roughly a kilobyte. That is real compressed data at a real ratio, and
/// it still costs a millisecond.
/// </para>
/// </remarks>
internal static class ZipFixtureBuilder
{
    /// <summary>Builds an archive containing the given entries, stored with default deflate.</summary>
    /// <param name="entries">Entry names and their uncompressed content, in order.</param>
    /// <returns>The archive bytes.</returns>
    public static byte[] Archive(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();

        // Scoped so the archive's central directory is written before the buffer is read.
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Zero bytes — the most compressible content there is.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The buffer.</returns>
    /// <remarks>
    /// A megabyte of these deflates to about a kilobyte, near deflate's theoretical ceiling of
    /// roughly 1032:1. <c>TheZeroFixtureIsGenuinelyAHighRatioEntry</c> asserts that rather than
    /// assuming it, so a runtime change to the compressor that quietly ruined the ratio would be a
    /// failure here rather than a bomb test that stopped testing bombs.
    /// </remarks>
    public static byte[] Zeros(int count) => new byte[count];

    /// <summary>
    /// Content deflate cannot usefully shrink, so an entry made of it has a ratio near 1:1.
    /// </summary>
    /// <param name="count">How many bytes.</param>
    /// <param name="seed">The generator seed, so the archive is byte-for-byte reproducible.</param>
    /// <returns>The buffer.</returns>
    /// <remarks>
    /// What the low-ratio, high-total case is made of: entries that no ratio bound can object to and
    /// that together exceed the archive's byte budget. Deterministic rather than
    /// <see cref="Random.Shared"/>, because a fixture that differs per run turns a boundary assertion
    /// into a flake.
    /// </remarks>
    public static byte[] Incompressible(int count, int seed)
    {
        var buffer = new byte[count];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }
}
