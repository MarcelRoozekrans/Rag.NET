using System.Globalization;
using System.Text.Json.Serialization;

namespace Rag.NET.Models;

/// <summary>
/// A chunk-metadata value that knows its own type: one of <see cref="string"/>,
/// <see cref="double"/>, <see cref="bool"/> or <see cref="System.DateTimeOffset"/>, discriminated
/// by <see cref="Kind"/>. Carrying the type end-to-end is what lets every vector store persist a
/// number as a number and filter on it numerically, instead of flattening everything to strings.
/// </summary>
/// <remarks>
/// <para>
/// Implicit conversions exist <b>from</b> each carried type, so write sites keep their shape:
/// <c>chunk.Metadata["page"] = 3</c> stores a <see cref="MetadataValueKind.Number"/> and
/// <c>chunk.Metadata["source"] = "crm"</c> a <see cref="MetadataValueKind.String"/>. There is
/// deliberately no implicit conversion <b>to</b> <see cref="string"/> — it would be lossy and
/// would silently paper over read sites that need to decide what a non-string value means to
/// them. Read through the kind-checked accessors (<see cref="StringValue"/> and siblings) or the
/// lossless-textual <see cref="ToString"/>.
/// </para>
/// <para>
/// Equality is by <see cref="Kind"/> and value: <c>(MetadataValue)"3"</c> and
/// <c>(MetadataValue)3</c> are <b>not</b> equal. Dates compare by instant, matching how they are
/// serialised (UTC-normalised; the original offset is not preserved).
/// </para>
/// </remarks>
[JsonConverter(typeof(MetadataValueJsonConverter))]
public readonly struct MetadataValue : IEquatable<MetadataValue>
{
    private readonly string? _string;
    private readonly double _number;
    private readonly bool _boolean;
    private readonly DateTimeOffset _dateTimeOffset;
    private readonly MetadataValueKind _kind;

    private MetadataValue(string value)
    {
        _string = value;
        _kind = MetadataValueKind.String;
    }

    private MetadataValue(double value)
    {
        _number = value;
        _kind = MetadataValueKind.Number;
    }

    private MetadataValue(bool value)
    {
        _boolean = value;
        _kind = MetadataValueKind.Boolean;
    }

    private MetadataValue(DateTimeOffset value)
    {
        _dateTimeOffset = value;
        _kind = MetadataValueKind.DateTimeOffset;
    }

    /// <summary>
    /// Which type this value carries. The uninitialised default is a
    /// <see cref="MetadataValueKind.String"/> holding <see cref="string.Empty"/>.
    /// </summary>
    public MetadataValueKind Kind => _kind;

    /// <summary>The carried string.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not <see cref="MetadataValueKind.String"/>.</exception>
    public string StringValue =>
        _kind == MetadataValueKind.String ? _string ?? string.Empty : throw KindMismatch(MetadataValueKind.String);

    /// <summary>The carried number.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not <see cref="MetadataValueKind.Number"/>.</exception>
    public double NumberValue =>
        _kind == MetadataValueKind.Number ? _number : throw KindMismatch(MetadataValueKind.Number);

    /// <summary>The carried boolean.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not <see cref="MetadataValueKind.Boolean"/>.</exception>
    public bool BooleanValue =>
        _kind == MetadataValueKind.Boolean ? _boolean : throw KindMismatch(MetadataValueKind.Boolean);

    /// <summary>The carried point in time.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not <see cref="MetadataValueKind.DateTimeOffset"/>.</exception>
    public DateTimeOffset DateTimeOffsetValue =>
        _kind == MetadataValueKind.DateTimeOffset ? _dateTimeOffset : throw KindMismatch(MetadataValueKind.DateTimeOffset);

    /// <summary>Wraps a string. A <see langword="null"/> reference becomes <see cref="string.Empty"/>.</summary>
    public static implicit operator MetadataValue(string value) => new(value ?? string.Empty);

    /// <summary>Wraps an <see cref="int"/> as a <see cref="MetadataValueKind.Number"/> (exact — every int fits a double).</summary>
    public static implicit operator MetadataValue(int value) => new((double)value);

    /// <summary>
    /// Wraps a <see cref="long"/> as a <see cref="MetadataValueKind.Number"/>. Exact up to 2^53;
    /// beyond that the nearest double is stored — the same rounding JSON itself would apply.
    /// </summary>
    public static implicit operator MetadataValue(long value) => new((double)value);

    /// <summary>Wraps a <see cref="double"/>.</summary>
    public static implicit operator MetadataValue(double value) => new(value);

    /// <summary>Wraps a <see cref="bool"/>.</summary>
    public static implicit operator MetadataValue(bool value) => new(value);

    /// <summary>Wraps a <see cref="System.DateTimeOffset"/>.</summary>
    public static implicit operator MetadataValue(DateTimeOffset value) => new(value);

    /// <summary>Value equality: same <see cref="Kind"/> and same carried value (dates by instant).</summary>
    public bool Equals(MetadataValue other)
    {
        if (_kind != other._kind)
            return false;

        return _kind switch
        {
            MetadataValueKind.Number => _number.Equals(other._number),
            MetadataValueKind.Boolean => _boolean == other._boolean,
            MetadataValueKind.DateTimeOffset => _dateTimeOffset.UtcTicks == other._dateTimeOffset.UtcTicks,
            _ => string.Equals(_string ?? string.Empty, other._string ?? string.Empty, StringComparison.Ordinal),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MetadataValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _kind switch
    {
        MetadataValueKind.Number => HashCode.Combine(_kind, _number),
        MetadataValueKind.Boolean => HashCode.Combine(_kind, _boolean),
        MetadataValueKind.DateTimeOffset => HashCode.Combine(_kind, _dateTimeOffset.UtcTicks),
        _ => HashCode.Combine(_kind, StringComparer.Ordinal.GetHashCode(_string ?? string.Empty)),
    };

    /// <summary>Value equality — see <see cref="Equals(MetadataValue)"/>.</summary>
    public static bool operator ==(MetadataValue left, MetadataValue right) => left.Equals(right);

    /// <summary>Value inequality — see <see cref="Equals(MetadataValue)"/>.</summary>
    public static bool operator !=(MetadataValue left, MetadataValue right) => !left.Equals(right);

    /// <summary>
    /// The value's invariant textual form, matching how the metadata serializer writes it:
    /// strings verbatim, numbers round-trippable invariant (<c>3</c>, <c>2.5</c>), booleans
    /// <c>true</c>/<c>false</c>, dates UTC ISO-8601. Lossless as text, but the <see cref="Kind"/>
    /// itself is not encoded — use the typed accessors when the type matters.
    /// </summary>
    public override string ToString() => _kind switch
    {
        MetadataValueKind.Number => _number.ToString(CultureInfo.InvariantCulture),
        MetadataValueKind.Boolean => _boolean ? "true" : "false",
        MetadataValueKind.DateTimeOffset => MetadataDateFormat.Format(_dateTimeOffset),
        _ => _string ?? string.Empty,
    };

    private InvalidOperationException KindMismatch(MetadataValueKind requested) =>
        new($"This metadata value carries a {_kind}, not a {requested}. " +
            $"Check {nameof(Kind)} before reading, or use {nameof(ToString)}() for a textual form of any kind.");
}
