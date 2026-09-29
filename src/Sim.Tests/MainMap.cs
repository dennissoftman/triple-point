using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sim.Tests;

/// <summary>
/// The playable map, godot/scenes/main.tscn, as SimHost builds it, read straight from the scene file:
/// its belts (curves and covered ends), starting buildings and units, and SimHost's belt settings, with
/// the real game data, and navigation over the camera's bounds. So tests can play on the map players play on.
/// </summary>
static class MainMap
{
    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "godot", "scenes", "main.tscn"))) return dir.FullName;
        throw new DirectoryNotFoundException("No repo root with godot/scenes/main.tscn above the test binaries.");
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    public static Simulation Load(uint seed = 1)
    {
        var repo = Repo();
        var scene = File.ReadAllText(Path.Combine(repo, "godot", "scenes", "main.tscn"));
        var weapons = GameData.ParseWeapons(File.ReadAllText(Path.Combine(repo, "data", "weapons.json")));
        var units = GameData.ParseUnitTypes(File.ReadAllText(Path.Combine(repo, "data", "units.json")), weapons);
        var buildings = GameData.ParseBuildingTypes(File.ReadAllText(Path.Combine(repo, "data", "buildings.json")), units);

        var sim = new Simulation(seed) { BuildingTypes = buildings, EndConditions = true };
        // The map is the camera's bounds, as SimHost takes it.
        var bounds = Regex.Match(scene, @"Bounds = Rect2\(([^)]*)\)").Groups[1].Value.Split(',').Select(F).ToArray();
        sim.EnableNavigation(bounds[0], bounds[1], bounds[0] + bounds[2], bounds[1] + bounds[3]);
        var host = Node(scene, "SimHost");
        float Setting(string name, float fallback) => Prop(host, name) is string v ? F(v) : fallback;
        int starting = (int)Setting("StartingPackages", 20);
        for (int p = 0; p < 2; p++) sim.State.Players[sim.AddPlayer()].Packages = starting;

        var belt = new BeltConfig(Setting("BeltSpeed", 1), Setting("PackageSpacing", 1), Setting("SpawnInterval", 2),
            Setting("SegmentLength", 5), Setting("SegmentHealth", 100), Setting("SpillLoss", 0.3f), (int)Setting("SourceSupply", 0));
        foreach (Match path in Regex.Matches(scene, @"\[node name=""[^""]+"" type=""Path3D"" parent=""Belts""\]\n((?:[^\[\n][^\n]*\n)*)"))
        {
            string body = path.Groups[1].Value;
            string curveId = Regex.Match(body, @"curve = SubResource\(""([^""]+)""\)").Groups[1].Value;
            var numbers = Regex.Match(scene, $@"id=""{Regex.Escape(curveId)}""\]\n_data = \{{\n""points"": PackedVector3Array\(([^)]*)\)")
                .Groups[1].Value.Split(',').Select(F).ToArray();
            // Per point: its in handle, out handle and position, as SimHost.ToSegments reads them.
            var points = Enumerable.Range(0, numbers.Length / 9).Select(i => (
                In: new Vector3(numbers[9 * i], numbers[9 * i + 1], numbers[9 * i + 2]),
                Out: new Vector3(numbers[9 * i + 3], numbers[9 * i + 4], numbers[9 * i + 5]),
                At: new Vector3(numbers[9 * i + 6], numbers[9 * i + 7], numbers[9 * i + 8]))).ToArray();
            var curves = Enumerable.Range(0, points.Length - 1)
                .Select(i => new BezierSegment(points[i].At, points[i].At + points[i].Out, points[i + 1].At + points[i + 1].In, points[i + 1].At)).ToArray();
            sim.AddBeltLine(curves, belt, Prop(body, "CoveredStart") is string cs ? F(cs) : 0, Prop(body, "CoveredEnd") is string ce ? F(ce) : 0);
        }

        foreach (var (body, position, heading) in Spawns(scene, "Obstacles", "Node3D"))
        {
            var size = Regex.Match(body, @"Size = Vector3\(([^)]*)\)").Groups[1].Value.Split(',').Select(F).ToArray();
            sim.AddObstacle(position, size[0] / 2, size[2] / 2, heading);
        }
        foreach (var (body, position, heading) in Spawns(scene, "Buildings"))
            sim.AddBuilding(Prop(body, "Player") is string p ? int.Parse(p) : 0, position, buildings[Prop(body, "BuildingType")?.Trim('"') ?? "hq"], heading);
        foreach (var (body, position, heading) in Spawns(scene, "Units"))
            sim.AddUnit(Prop(body, "Player") is string p ? int.Parse(p) : 0, position, units[Prop(body, "UnitType")?.Trim('"') ?? "rifle_squad"], heading);
        return sim;
    }

    // Nodes under a parent (markers, unless said): each one's properties, its position, and the heading its -Z faces.
    static IEnumerable<(string Body, Vector3 Position, float Heading)> Spawns(string scene, string parent, string type = "Marker3D")
    {
        foreach (Match m in Regex.Matches(scene, $@"\[node name=""[^""]+"" type=""{type}"" parent=""{parent}""\]\n((?:[^\[\n][^\n]*\n)*)"))
        {
            var body = m.Groups[1].Value;
            var t = Regex.Match(body, @"transform = Transform3D\(([^)]*)\)").Groups[1].Value.Split(',').Select(F).ToArray();
            var z = new Vector3(t[2], t[5], t[8]); // the basis' Z column (Transform3D lists the basis row by row)
            yield return (body, new Vector3(t[9], t[10], t[11]), MathF.Atan2(-z.X, -z.Z));
        }
    }

    static string Node(string scene, string name) =>
        Regex.Match(scene, $@"\[node name=""{name}""[^\n]*\]\n((?:[^\[\n][^\n]*\n)*)").Groups[1].Value;

    static string? Prop(string body, string name) =>
        Regex.Match(body, $@"^{name} = (.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
}
