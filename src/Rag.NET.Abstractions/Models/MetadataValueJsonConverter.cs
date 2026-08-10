using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.NET.Models;

/// <summary>
/// JSON round-trip for <see cref="MetadataValue"/>, applied automatically wherever one is
/// serialized with System.Text.Json (it is declared on the type).
/// </summary>
/// <remarks>
/// <para>
/// Strings, numbers and booleans are written as the corresponding native JSON value. Dates are
/// written as <c>{"$date": "&lt;UTC ISO-8601&gt;"}</c> — a wrapper object rather than a bare
/// string, because a bare string would be indistinguishable from a
/// <see cref="MetadataValueKind.String"/> that merely looks like a date.
/// </para>
/// <para>
/// Reading is deliberately lenient, because it is also the migration path for metadata written
/// before values carried types: any JSON string reads back as a
/// <see cref="MetadataValueKind.String"/>, which is exactly what every pre-typed-metadata
/// document contains. JSON shapes this converter never writes (arrays, other objects) also read
/// back as strings, carrying their raw JSON text, rather than failing the whole document.
/// </para>
/// </remarks>
public sealed class MetadataValueJsonConverter : JsonConverter<MetadataValue>
{
    /// <inheritdoc />
    public override MetadataValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => string.Empty,
            _ => ReadStructured(ref reader),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MetadataValue value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Number:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case MetadataValueKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue);
                break;
            case MetadataValueKind.DateTimeOffset:
                writer.WriteStartObject();
                writer.WriteString(MetadataDateFormat.JsonPropertyName, MetadataDateFormat.Format(value.DateTimeOffsetValue));
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue(value.StringValue);
                break;
        }
    }

    private static MetadataValue ReadStructured(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        if (element.ValueKind == JsonValueKind.Object && TryGetSingleDateProperty(element, out var date))
            return date;

        // Unknown shape (array, other object): keep the raw JSON as a string instead of
        // failing the whole metadata dictionary. This converter never writes such shapes.
        return element.GetRawText();
    }

    private static bool TryGetSingleDateProperty(JsonElement element, out DateTimeOffset date)
    {
        date = default;
        var count = 0;
        JsonElement value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(MetadataDateFormat.JsonPropertyName))
                return false;
            value = property.Value;
            count++;
        }

        return count == 1
            && value.ValueKind == JsonValueKind.String
            && MetadataDateFormat.TryParse(value.GetString()!, out date);
    }
}
