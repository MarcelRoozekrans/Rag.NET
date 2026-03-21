using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(DocumentIdJsonConverter))]
[ValueObject]
public sealed partial class DocumentId
{
    private readonly string _value;

    [EqualityMember]
    public string Value => _value;

    public DocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public override string ToString() => _value;

    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string s) => new(s);

    private sealed class DocumentIdJsonConverter : JsonConverter<DocumentId>
    {
        public override DocumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrEmpty(value))
                throw new JsonException("DocumentId cannot be null or empty.");
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, DocumentId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
