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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelled or undocumented field fails loudly
    };

    /// <summary>Weapon types by id, from the contents of weapons.json. Each knows its own id.</summary>
    public static Dictionary<string, WeaponType> ParseWeapons(string json)
    {
        var weapons = JsonSerializer.Deserialize<Dictionary<string, WeaponType>>(json, Options)
            ?? throw new InvalidDataException("weapons.json has no weapons.");
        foreach (var (id, w) in weapons)
        {
            if (w.Reload <= 0 || w.Range <= 0 || (w.Kind == WeaponKind.Shell && w.ShellSpeed <= 0) || (w.Hit == HitKind.Splash) != (w.SplashRadius > 0))
                throw new InvalidDataException(
                    $"Weapon '{id}' needs a reload and range above 0, a shell a shellSpeed above 0, and a splashRadius above 0 exactly when its hit is splash.");
            if (w.MinRange < 0 || w.MinRange >= w.Range || w.Scatter < 0 || ((w.Ballistic || w.Scatter > 0) && w.Kind != WeaponKind.Shell))
                throw new InvalidDataException($"Weapon '{id}' needs a minRange from 0 up to below its range, and ballistic and scatter only on a shell.");
            if (w.StructureDamage < 0) throw new InvalidDataException($"Weapon '{id}' needs a structureDamage of at least 0.");
        }
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
        foreach (var (id, t) in types)
            if (t.Cost < 0 || t.BuildTime < 0) throw new InvalidDataException($"Unit type '{id}' needs a cost and buildTime of at least 0.");
        return types.ToDictionary(t => t.Key, t => string.IsNullOrEmpty(t.Value.Weapon)
            ? t.Value with { Id = t.Key, Gun = WeaponType.Unarmed }
            : weapons.TryGetValue(t.Value.Weapon, out var gun)
                ? t.Value with { Id = t.Key, Gun = gun }
                : throw new InvalidDataException($"Unit type '{t.Key}' has unknown weapon '{t.Value.Weapon}'."));
    }

    /// <summary>
    /// Building types by id, from the contents of buildings.json, with the unit types each produces (and a
    /// defense becomes) looked up in `units`. Each type knows its own id. Also checks that what the unit
    /// types build exists.
    /// </summary>
    public static Dictionary<string, BuildingType> ParseBuildingTypes(string json, IReadOnlyDictionary<string, UnitType> units)
    {
        var types = JsonSerializer.Deserialize<Dictionary<string, BuildingType>>(json, Options)
            ?? throw new InvalidDataException("buildings.json has no building types.");
        var parsed = new Dictionary<string, BuildingType>();
        foreach (var (id, t) in types)
        {
            if (t.Health <= 0 || t.Size <= 0 || t.Cost < 0 || t.BuildTime < 0)
                throw new InvalidDataException($"Building type '{id}' needs a health and size above 0, and a cost and buildTime of at least 0.");
            if (t.Mends is TargetClass.Any || t.MendSeconds <= 0 || t.MendShare < 0)
                throw new InvalidDataException($"Building type '{id}' mends infantry or vehicles (not any), in a mendSeconds above 0 for a mendShare of at least 0.");
            var produces = (t.Produces ?? []).Select(u => units.TryGetValue(u, out var type) ? type
                : throw new InvalidDataException($"Building type '{id}' produces unknown unit type '{u}'.")).ToArray();
            UnitType? defense = null;
            if (t.Kind == BuildingKind.Defense && (t.Unit is null || !units.TryGetValue(t.Unit, out defense)))
                throw new InvalidDataException($"Defense '{id}' needs a unit, an id in units.json (got '{t.Unit}').");
            if (t.Kind != BuildingKind.Building && produces.Length > 0)
                throw new InvalidDataException($"Building type '{id}' becomes a {t.Kind} when finished, so it can't produce units.");
            parsed[id] = t with { Id = id, Produces = t.Produces ?? [], Units = produces, Defense = defense };
        }
        foreach (var t in parsed.Values)
            if (t.Requires is not null && !parsed.ContainsKey(t.Requires))
                throw new InvalidDataException($"Building type '{t.Id}' requires unknown building type '{t.Requires}'.");
        foreach (var unit in units.Values)
            foreach (string b in unit.Builds ?? [])
                if (!parsed.ContainsKey(b)) throw new InvalidDataException($"Unit type '{unit.Id}' builds unknown building type '{b}'.");
        return parsed;
    }
}
