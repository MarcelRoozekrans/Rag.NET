using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class SharedServiceTypesTests
{
    [Fact]
    public void Entries_WhenNothingAdded_IsEmpty()
    {
        var sut = new SharedServiceTypes();

        Assert.Empty(sut.Entries);
    }

    [Fact]
    public void AddRange_RecordsEachDescriptorsServiceType()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([
            ServiceDescriptor.Singleton(typeof(string), "a"),
            ServiceDescriptor.Singleton(typeof(int), 1),
        ]);

        Assert.Equal([typeof(string), typeof(int)], sut.Entries.Select(e => e.ServiceType));
    }

    // Two AddRagNetShared calls are legal; the second must not lose the first's types.
    [Fact]
    public void AddRange_CalledTwice_KeepsBoth()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([ServiceDescriptor.Singleton(typeof(string), "a")]);
        sut.AddRange([ServiceDescriptor.Singleton(typeof(int), 1)]);

        Assert.Equal([typeof(string), typeof(int)], sut.Entries.Select(e => e.ServiceType));
    }

    // The same service type declared shared twice must forward once, or the child collection
    // gets duplicate descriptors and IEnumerable<T> resolution silently doubles.
    [Fact]
    public void AddRange_WithADuplicateType_RecordsItOnce()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([ServiceDescriptor.Singleton(typeof(string), "a")]);
        sut.AddRange([ServiceDescriptor.Singleton(typeof(string), "b")]);

        Assert.Equal([typeof(string)], sut.Entries.Select(e => e.ServiceType));
    }

    /// <summary>C1: the lifetime and keyed-ness recorded per entry are what <c>BuildFactory</c>
    /// filters on, so they must reflect the declaring descriptor, not a default.</summary>
    [Fact]
    public void AddRange_RecordsLifetimeAndKeyedFromTheDescriptor()
    {
        var sut = new SharedServiceTypes();

        sut.AddRange([
            ServiceDescriptor.Transient(typeof(string), _ => "a"),
            ServiceDescriptor.KeyedSingleton(typeof(int), "key", 1),
        ]);

        var stringEntry = Assert.Single(sut.Entries, e => e.ServiceType == typeof(string));
        Assert.Equal(ServiceLifetime.Transient, stringEntry.Lifetime);
        Assert.False(stringEntry.IsKeyed);

        var intEntry = Assert.Single(sut.Entries, e => e.ServiceType == typeof(int));
        Assert.True(intEntry.IsKeyed);
    }
}
