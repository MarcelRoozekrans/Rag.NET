using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.Models;

[JsonConverter(typeof(DocumentIdJsonConverter))]
public sealed class DocumentId : IEquatable<DocumentId>
{
    private readonly string _value;

    public DocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public override string ToString() => _value;

    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string s) => new(s);

    public bool Equals(DocumentId? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DocumentId other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(DocumentId? left, DocumentId? right)
        => left?.Equals(right) ?? right is null;

    public static bool operator !=(DocumentId? left, DocumentId? right)
        => !(left == right);

    private sealed class DocumentIdJsonConverter : JsonConverter<DocumentId>
    {
        public override DocumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, DocumentId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
