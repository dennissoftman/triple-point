using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim;

/// <summary>Loads game data from the JSON files in /data. The host reads the files; this only parses.</summary>
public static class GameData
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }, // "movement": "tracked"
    };

    /// <summary>Unit types by id, from the contents of units.json. Each type knows its own id.</summary>
    public static Dictionary<string, UnitType> ParseUnitTypes(string json)
    {
        var types = JsonSerializer.Deserialize<Dictionary<string, UnitType>>(json, Options)
            ?? throw new InvalidDataException("units.json has no unit types.");
        return types.ToDictionary(t => t.Key, t => t.Value with { Id = t.Key });
    }
}
