using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

/// <summary>
/// The typed metadata value (#91): each implicit conversion stores the matching
/// <see cref="MetadataValueKind"/>, equality is kind-sensitive (the whole point — a number and
/// the string that spells it are different values), and the kind-checked accessors refuse the
/// wrong kind instead of silently stringifying.
/// </summary>
public class MetadataValueTests
{
    [Fact]
    public void ImplicitConversions_StoreTheMatchingKind()
    {
        MetadataValue s = "text";
        MetadataValue i = 3;
        MetadataValue l = 3L;
        MetadataValue d = 2.5;
        MetadataValue b = true;
        MetadataValue date = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(MetadataValueKind.String, s.Kind);
        Assert.Equal(MetadataValueKind.Number, i.Kind);
        Assert.Equal(MetadataValueKind.Number, l.Kind);
        Assert.Equal(MetadataValueKind.Number, d.Kind);
        Assert.Equal(MetadataValueKind.Boolean, b.Kind);
        Assert.Equal(MetadataValueKind.DateTimeOffset, date.Kind);

        Assert.Equal("text", s.StringValue);
        Assert.Equal(3d, i.NumberValue);
        Assert.Equal(2.5, d.NumberValue);
        Assert.True(b.BooleanValue);
        Assert.Equal(2026, date.DateTimeOffsetValue.Year);
    }

    [Fact]
    public void Equality_IsKindSensitive()
    {
        // "3" and 3 not being equal is the design: a store that flattened the number to text
        // would produce a value that no longer compares equal to what was written.
        Assert.NotEqual((MetadataValue)"3", (MetadataValue)3);
        Assert.NotEqual((MetadataValue)"true", (MetadataValue)true);
        Assert.Equal((MetadataValue)3, (MetadataValue)3.0);
        Assert.Equal((MetadataValue)"a", (MetadataValue)"a");
    }

    [Fact]
    public void Equality_Dates_CompareByInstant()
    {
        var utc = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var sameInstantOffset = new DateTimeOffset(2026, 5, 4, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal((MetadataValue)utc, (MetadataValue)sameInstantOffset);
    }

    [Fact]
    public void Accessors_WrongKind_Throw()
    {
        MetadataValue number = 3;

        var ex = Assert.Throws<InvalidOperationException>(() => number.StringValue);
        Assert.Contains("Number", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => number.BooleanValue);
        Assert.Throws<InvalidOperationException>(() => number.DateTimeOffsetValue);
    }

    [Fact]
    public void Default_IsEmptyString()
    {
        var value = default(MetadataValue);

        Assert.Equal(MetadataValueKind.String, value.Kind);
        Assert.Equal(string.Empty, value.StringValue);
    }

    [Fact]
    public void ToString_IsInvariantTextualForm()
    {
        Assert.Equal("text", ((MetadataValue)"text").ToString());
        Assert.Equal("3", ((MetadataValue)3).ToString());
        Assert.Equal("2.5", ((MetadataValue)2.5).ToString());
        Assert.Equal("true", ((MetadataValue)true).ToString());
        Assert.Equal("false", ((MetadataValue)false).ToString());
    }

    [Fact]
    public void NullString_BecomesEmptyString()
    {
        MetadataValue value = (string)null!;

        Assert.Equal(MetadataValueKind.String, value.Kind);
        Assert.Equal(string.Empty, value.StringValue);
    }
}
