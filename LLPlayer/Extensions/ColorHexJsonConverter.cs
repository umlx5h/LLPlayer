using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace LLPlayer.Extensions;

// Convert Color object to HEX string
public class ColorHexJsonConverter : JsonConverter<Color>
{
    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        string hex = $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}";
        writer.WriteStringValue(hex);
    }

    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? hex = null;
        try
        {
            hex = reader.GetString();

            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new JsonException("Color value is null or empty.");
            }

            if (!hex.StartsWith("#") || (hex.Length != 7 && hex.Length != 9))
            {
                throw new JsonException($"Invalid color format: {hex}");
            }
            byte a = 255;

            int start = 1;

            if (hex.Length == 9)
            {
                a = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber);
                start = 3;
            }

            byte r = byte.Parse(hex.Substring(start, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(start + 2, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(start + 4, 2), NumberStyles.HexNumber);

            return Color.FromArgb(a, r, g, b);
        }
        catch (Exception ex)
        {
            throw new JsonException($"Error parsing color value: {hex}", ex);
        }
    }
}
