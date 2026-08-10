using System.Globalization;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins the one string that lets the ablation table's HyDE row find the text the generation tool
/// wrote. The tool hashed <c>openai/gpt-4o-mini@t0.8</c> into all 7,062 cache keys of the frozen
/// generation run; a formatting change here — a renamed model, a culture-sensitive float, a moved
/// <c>@t</c> — silently orphans every one of them, and the first symptom would be a refuse-on-miss
/// failure blaming a cache that is actually intact.
/// </summary>
public sealed class HypotheticalModelIdentityTests
{
    [Fact]
    public void For_AtTheLibraryDefaultTemperature_IsTheIdentityTheGenerationRunWasKeyedOn()
    {
        // 0.8f is HydeOptions.HypothesisTemperature's default — the setting the generation run
        // used. This literal is the anchor: the cache on disk is immutable, so the string that
        // addresses it must be too.
        Assert.Equal("openai/gpt-4o-mini@t0.8", HypotheticalModelIdentity.For(0.8f));
    }

    [Fact]
    public void For_FormatsTheTemperatureInvariantly_WhateverTheThreadCulture()
    {
        // A culture that writes 0,8 would compute a key no generation run ever wrote under —
        // the whole cache misses on machines with a comma decimal separator, and only there.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            Assert.Equal("openai/gpt-4o-mini@t0.8", HypotheticalModelIdentity.For(0.8f));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void For_ChangesWhenTheTemperatureChanges_SoResampledTextNeverSharesEntries()
    {
        // Temperature is in the key because it changes the output distribution: a run resampled
        // at another temperature must regenerate, not silently reuse the old run's text.
        Assert.NotEqual(
            HypotheticalModelIdentity.For(0.8f), HypotheticalModelIdentity.For(0f), StringComparer.Ordinal);
    }
}
