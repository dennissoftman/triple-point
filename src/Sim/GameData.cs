using System.Text.Json;

namespace Sim;

/// <summary>Loads game data from the JSON files in /data. The host reads the files; this only parses.</summary>
public static class GameData
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Unit types by id, from the contents of units.json.</summary>
    public static Dictionary<string, UnitType> ParseUnitTypes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, UnitType>>(json, Options)
        ?? throw new InvalidDataException("units.json has no unit types.");
}
