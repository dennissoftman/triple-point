using System.Text.RegularExpressions;

namespace Sim.Tests;

/// <summary>
/// The game's text (godot/locale/en.po) against the code that shows it (godot/scripts): every key the
/// scripts use exists in English, every English key is used, and every unit and building in /data has a
/// name. Other languages may lack keys (they fall back to English) but may not have keys English lacks.
/// </summary>
public class LocaleTests
{
    // Key families: a quoted string in the scripts starting with one of these is a key.
    static readonly string[] Families = ["side", "unit", "building", "key", "hud", "packages", "card", "place", "belt", "truck", "gauge", "gameover", "menu", "alert", "select", "order", "idle"];

    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "godot", "locale", "en.po"))) return dir.FullName;
        throw new DirectoryNotFoundException("No repo root with godot/locale/en.po above the test binaries.");
    }

    static Dictionary<string, string> Po(string path)
    {
        var entries = new Dictionary<string, string>();
        string? id = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("msgid \"")) id = line[7..^1];
            else if (line.StartsWith("msgstr \"") && id is { Length: > 0 }) entries[id] = line[8..^1];
        }
        return entries;
    }

    static SortedSet<string> UsedKeys()
    {
        var pattern = new Regex("\"((?:" + string.Join("|", Families) + @")\.[a-z0-9_.]+)""");
        var keys = new SortedSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo(), "godot", "scripts"), "*.cs"))
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                if (!m.Groups[1].Value.EndsWith('.')) keys.Add(m.Groups[1].Value);
        return keys;
    }

    static IEnumerable<string> DataNames()
    {
        var weapons = GameData.ParseWeapons(File.ReadAllText(Path.Combine(Repo(), "data", "weapons.json")));
        var units = GameData.ParseUnitTypes(File.ReadAllText(Path.Combine(Repo(), "data", "units.json")), weapons);
        var buildings = GameData.ParseBuildingTypes(File.ReadAllText(Path.Combine(Repo(), "data", "buildings.json")), units);
        return units.Keys.Select(id => "unit." + id).Concat(buildings.Keys.Select(id => "building." + id));
    }

    static void None(IEnumerable<string> keys, string what)
    {
        var list = keys.ToList();
        Assert.True(list.Count == 0, $"{what}: {string.Join(", ", list)}");
    }

    // Godot won't load a .po with a line it can't parse, and then shows no text at all: every line is a
    // comment, blank, or a msgid/msgstr whose string closes on the same line (a line break is written \n).
    [Fact]
    public void Every_translation_file_is_well_formed()
    {
        var entry = new Regex(@"^(msgid|msgstr) ""([^""\\]|\\.)*""$");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo(), "godot", "locale"), "*.po"))
        {
            int n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("\"")) continue;
                Assert.True(entry.IsMatch(line), $"{Path.GetFileName(file)}:{n}: {line}");
            }
        }
    }

    [Fact]
    public void Every_key_the_scripts_use_is_in_english()
    {
        var english = Po(Path.Combine(Repo(), "godot", "locale", "en.po"));
        None(UsedKeys().Where(k => !english.ContainsKey(k)), "used but not in en.po");
    }

    [Fact]
    public void Every_unit_and_building_has_a_name()
    {
        var english = Po(Path.Combine(Repo(), "godot", "locale", "en.po"));
        None(DataNames().Where(k => !english.ContainsKey(k)), "no name in en.po");
    }

    [Fact]
    public void Every_english_key_is_used()
    {
        var used = UsedKeys().Concat(DataNames()).ToHashSet();
        var english = Po(Path.Combine(Repo(), "godot", "locale", "en.po"));
        None(english.Keys.Where(k => !used.Contains(k)), "in en.po but never used");
        Assert.All(english, e => Assert.NotEqual("", e.Value));
    }

    [Fact]
    public void Other_languages_have_no_keys_english_lacks()
    {
        var english = Po(Path.Combine(Repo(), "godot", "locale", "en.po"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo(), "godot", "locale"), "*.po"))
            None(Po(file).Keys.Where(k => !english.ContainsKey(k)), Path.GetFileName(file) + " has keys en.po lacks");
    }
}
