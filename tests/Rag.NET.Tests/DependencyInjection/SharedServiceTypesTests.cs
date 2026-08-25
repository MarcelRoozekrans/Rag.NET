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
