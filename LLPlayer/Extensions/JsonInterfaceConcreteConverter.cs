using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLPlayer.Extensions;

/// <summary>
/// JsonConverter to serialize and deserialize interfaces with concrete types using a mapping between interfaces and concrete types
/// </summary>
/// <typeparam name="T"></typeparam>
public class JsonInterfaceConcreteConverter<T> : JsonConverter<T>
{
    private const string TypeKey = "TypeName";
    private readonly Dictionary<string, Type> _typeMapping;

    public JsonInterfaceConcreteConverter(Dictionary<string, Type> typeMapping)
    {
        _typeMapping = typeMapping;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument jsonDoc = JsonDocument.ParseValue(ref reader);

        if (!jsonDoc.RootElement.TryGetProperty(TypeKey, out JsonElement typeProperty))
        {
            throw new JsonException("Type discriminator not found.");
        }

        string? typeDiscriminator = typeProperty.GetString();
        if (typeDiscriminator == null || !_typeMapping.TryGetValue(typeDiscriminator, out Type? targetType))
        {
            throw new JsonException($"Unknown type discriminator: {typeDiscriminator}");
        }

        // If a specific type is specified as the second argument, it is deserialized with that type
        return (T)JsonSerializer.Deserialize(jsonDoc.RootElement, targetType, options)!;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Type type = value!.GetType();
        string typeDiscriminator = type.Name; // Use type name as discriminator

        // Serialize with concrete types, not interfaces
        string json = JsonSerializer.Serialize(value, type, options);
        using JsonDocument jsonDoc = JsonDocument.Parse(json);

        writer.WriteStartObject();
        // Save concrete type name
        writer.WriteString(TypeKey, typeDiscriminator);

        // Does this work even if it's nested?
        foreach (JsonProperty property in jsonDoc.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
