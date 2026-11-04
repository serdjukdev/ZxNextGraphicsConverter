using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZxNext.Core.Project;

/// <summary>
/// Reads an object definition's cell in EITHER shape. A cell used to be just the sprite's id (a bare GUID
/// string, or null for an empty cell); it is now an object carrying that id plus the cell's pixel offset.
/// Without this, opening a project saved by the in-between version threw a JsonException, which took the
/// whole project down with it, over a feature the file hardly used.
///
/// <para>
/// This is the general rule for this manifest: the writer only ever emits the current shape, and the
/// reader accepts every shape that was ever written. Nothing else in the file needed it so far because
/// every earlier change only ADDED optional properties, which System.Text.Json already tolerates in both
/// directions; this one changed a property's element type, which it does not.
/// </para>
/// </summary>
public class ObjectCellDtoJsonConverter : JsonConverter<ObjectCellDto>
{
    /// <summary>
    /// Without this, System.Text.Json never calls a reference type's converter for a JSON null: it puts a
    /// null straight into the list and the Null branch below is dead code. The oldest shape wrote an empty
    /// cell as exactly that bare null, so reading one of those files crashed on the first empty cell.
    /// </summary>
    public override bool HandleNull => true;

    public override ObjectCellDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            // The old shape: an empty cell was a bare null.
            case JsonTokenType.Null:
                return new ObjectCellDto();

            // The old shape: a filled cell was the sprite asset's GUID.
            case JsonTokenType.String:
                return new ObjectCellDto
                {
                    SpriteAssetId = Guid.TryParse(reader.GetString(), out var legacyId) ? legacyId : null
                };

            case JsonTokenType.StartObject:
                return ReadObject(ref reader);

            default:
                throw new JsonException($"An object definition's cell cannot be read from a {reader.TokenType} token.");
        }
    }

    private static ObjectCellDto ReadObject(ref Utf8JsonReader reader)
    {
        var cell = new ObjectCellDto();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return cell;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            if (!reader.Read()) break;

            if (string.Equals(name, nameof(ObjectCellDto.SpriteAssetId), StringComparison.OrdinalIgnoreCase))
            {
                cell.SpriteAssetId = reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id) ? id : null;
            }
            else if (string.Equals(name, nameof(ObjectCellDto.OffsetX), StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Number) cell.OffsetX = reader.GetInt32();
            }
            else if (string.Equals(name, nameof(ObjectCellDto.OffsetY), StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Number) cell.OffsetY = reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        return cell;
    }

    public override void Write(Utf8JsonWriter writer, ObjectCellDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.SpriteAssetId is { } id) writer.WriteString(nameof(ObjectCellDto.SpriteAssetId), id);
        else writer.WriteNull(nameof(ObjectCellDto.SpriteAssetId));
        writer.WriteNumber(nameof(ObjectCellDto.OffsetX), value.OffsetX);
        writer.WriteNumber(nameof(ObjectCellDto.OffsetY), value.OffsetY);
        writer.WriteEndObject();
    }
}
