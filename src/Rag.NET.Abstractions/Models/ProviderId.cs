using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(ProviderIdJsonConverter))]
[ValueObject]
public sealed partial class ProviderId
{
    private readonly string _value;

    [EqualityMember]
    public string Value => _value;

    public ProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public static implicit operator string(ProviderId id) => id._value;
    public static explicit operator ProviderId(string s) => new(s);

    private sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
    {
        public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrEmpty(value))
                throw new JsonException("ProviderId cannot be null or empty.");
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
