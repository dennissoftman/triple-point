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

    /// <summary>Weapon types by id, from the contents of weapons.json. Each knows its own id.</summary>
    public static Dictionary<string, WeaponType> ParseWeapons(string json)
    {
        var weapons = JsonSerializer.Deserialize<Dictionary<string, WeaponType>>(json, Options)
            ?? throw new InvalidDataException("weapons.json has no weapons.");
        foreach (var (id, w) in weapons)
            if (w.Reload <= 0 || w.Range <= 0 || (w.Kind == WeaponKind.Shell && w.ShellSpeed <= 0) || (w.Hit == HitKind.Splash) != (w.SplashRadius > 0))
                throw new InvalidDataException(
                    $"Weapon '{id}' needs a reload and range above 0, a shell a shellSpeed above 0, and a splashRadius above 0 exactly when its hit is splash.");
        return weapons.ToDictionary(w => w.Key, w => w.Value with { Id = w.Key });
    }

    /// <summary>
    /// Unit types by id, from the contents of units.json, with each type's weapon looked up in `weapons`.
    /// Each type knows its own id.
    /// </summary>
    public static Dictionary<string, UnitType> ParseUnitTypes(string json, IReadOnlyDictionary<string, WeaponType> weapons)
    {
        var types = JsonSerializer.Deserialize<Dictionary<string, UnitType>>(json, Options)
            ?? throw new InvalidDataException("units.json has no unit types.");
        return types.ToDictionary(t => t.Key, t => weapons.TryGetValue(t.Value.Weapon ?? "", out var gun)
            ? t.Value with { Id = t.Key, Gun = gun }
            : throw new InvalidDataException($"Unit type '{t.Key}' has unknown weapon '{t.Value.Weapon}'."));
    }
}
