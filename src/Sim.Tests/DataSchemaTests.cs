using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// The game data in /data against its parser and its documentation (docs/data.md): the real files load,
/// unknown fields fail, and the documented fields are exactly the ones the records take.
/// </summary>
public class DataSchemaTests
{
    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "units.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("No repo root with data/units.json above the test binaries.");
    }

    static string Read(params string[] path) => File.ReadAllText(Path.Combine([Repo(), .. path]));

    static (Dictionary<string, WeaponType>, Dictionary<string, UnitType>, Dictionary<string, BuildingType>) Load()
    {
        var weapons = GameData.ParseWeapons(Read("data", "weapons.json"));
        var units = GameData.ParseUnitTypes(Read("data", "units.json"), weapons);
        return (weapons, units, GameData.ParseBuildingTypes(Read("data", "buildings.json"), units));
    }

    // The JSON field names a record takes: its constructor's parameters, camelCase, less Id (the file's key).
    static SortedSet<string> Fields(Type record) =>
        new(record.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().GetParameters()
            .Select(p => char.ToLowerInvariant(p.Name![0]) + p.Name[1..]).Where(n => n != "id"));

    // The fields in a section's table of docs/data.md: the first column of each row, in backticks.
    static SortedSet<string> Documented(string file)
    {
        var doc = Read("docs", "data.md");
        int start = doc.IndexOf($"## {file}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/data.md has no section for {file}");
        int end = doc.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? doc[start..] : doc[start..end];
        return new(Regex.Matches(section, @"^\| `([a-zA-Z]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
    }

    [Theory]
    [InlineData("weapons.json", typeof(WeaponType))]
    [InlineData("units.json", typeof(UnitType))]
    [InlineData("buildings.json", typeof(BuildingType))]
    public void Every_field_is_documented_and_every_documented_field_exists(string file, Type record) =>
        Assert.Equal(Fields(record), Documented(file));

    [Fact]
    public void The_data_files_load()
    {
        var (weapons, units, buildings) = Load();
        Assert.Contains("artillery_gun", weapons.Keys);
        Assert.Contains("engineer", units.Keys);
        Assert.Contains("artillery", buildings["factory"].Produces!);
    }

    [Fact]
    public void An_unknown_field_fails_to_load()
    {
        var weapons = GameData.ParseWeapons("""{ "gun": { "kind": "bullet", "damage": 1, "reload": 1, "range": 5 } }""");
        Assert.ThrowsAny<Exception>(() => GameData.ParseUnitTypes("""{ "squad": { "members": 1, "speed": 1, "memberHealth": 1, "repairCosts": 2 } }""", weapons));
    }

    [Fact]
    public void A_barracks_costs_exactly_its_cost_and_spending_is_counted()
    {
        var (_, units, buildings) = Load();
        var sim = NewSim();
        sim.BuildingTypes = buildings;
        var barracks = buildings["barracks"];
        sim.State.Players[Blue].Packages = barracks.Cost + 4;
        int builder = sim.AddUnit(Blue, Vector3.Zero, units["builder"]);

        sim.Tick([new BuildCommand(Blue, builder, "barracks", new Vector3(12, 0, 0))]);
        int done = -1;
        for (int t = 0; t < barracks.BuildTicks + 40 * T && done < 0; t++)
            foreach (var e in sim.Tick(NoCommands))
                if (e.Kind == SimEventKind.BuildingCompleted) done = t;

        Assert.True(done >= 0);
        Assert.Equal(4, sim.State.Players[Blue].Packages);
        Assert.Equal(barracks.Cost, sim.State.Players[Blue].Spent);
    }
}
