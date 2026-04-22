using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(SessionIdJsonConverter))]
[ValueObject]
public sealed partial class SessionId
{
    private readonly string _value;

    [EqualityMember]
    public string Value => _value;

    public SessionId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public static implicit operator string(SessionId id) => id._value;
    public static explicit operator SessionId(string s) => new(s);

    private sealed class SessionIdJsonConverter : JsonConverter<SessionId>
    {
        public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrEmpty(value))
                throw new JsonException("SessionId cannot be null or empty.");
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
