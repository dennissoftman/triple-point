using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Sim;

/// <summary>
/// The whole state as text, one field per line ("units#57.Position = ..."), for finding where two
/// machines' states parted after a desync: Compare lists the first lines that differ. Entities are named
/// by id where they have one, so a unit missing on one side doesn't shift every line after it. Floats show
/// their bits too: a desync is usually a last-bit difference. Written by reflection, so every field is in
/// it without a list to keep up; slow, and only used after a desync.
/// </summary>
public static class StateDump
{
    public static string Write(Simulation sim)
    {
        var s = sim.State;
        var text = new StringBuilder();
        void Line(string key, object? value) => text.Append(key).Append(" = ").Append(Format(value)).Append('\n');
        void Fields(string key, object item)
        {
            foreach (var f in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Line($"{key}.{Name(f)}", f.GetValue(item));
        }

        Line("tick", s.Tick);
        Line("sim.hash", sim.Hash().All.ToString("x16"));
        Line("sim.gameOver", s.GameOver);
        Line("sim.winner", s.Winner);
        foreach (var p in s.Players) Fields($"players#{p.Index}", p);
        foreach (var u in s.Units)
        {
            Fields($"units#{u.Id}", u);
            if (sim.PathOf(u, out int next) is { } path) Line($"units#{u.Id}.path", $"{next} of {Format(path)}");
        }
        for (int l = 0; l < s.Belts.Count; l++)
        {
            var line = s.Belts[l];
            foreach (var f in line.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType != typeof(BeltSegment[]) && f.FieldType != typeof(List<Package>)) Line($"roads#{l}.{Name(f)}", f.GetValue(line));
            for (int g = 0; g < line.Segments.Length; g++) Fields($"roads#{l}.pieces#{g}", line.Segments[g]);
            foreach (var t in line.Packages) Fields($"trucks#{t.Id}", t);
        }
        foreach (var b in s.Buildings) Fields($"buildings#{b.Id}", b);
        foreach (var g in s.Gatherers) Fields($"depots#{g.Id}", g);
        foreach (var p in s.Projectiles) Fields($"shots#{p.Id}", p);
        foreach (var p in s.Pickups) Fields($"pickups#{p.Id}", p);
        for (int p = 0; p < s.Players.Count; p++)
        {
            var v = sim.Vision(p);
            var seen = new StateHasher();
            foreach (int t in v.Seen) seen.Add(t);
            Line($"vision#{p}.seen", $"{v.Seen.Length} cells, hash {seen.Value:x16}");
            foreach (var g in v.Ghosts) Fields($"vision#{p}.ghosts#{g.Id}", g);
            for (int l = 0; l < v.SegmentStates.Length; l++)
                Line($"vision#{p}.roads#{l}", string.Join(" ", v.SegmentStates[l].Zip(v.SegmentHealth[l], (st, h) => $"{st}:{Format(h)}")));
        }
        return text.ToString();
    }

    /// <summary>
    /// The first `max` keys whose values differ, with both values; a thing (a unit, a truck...) only one
    /// side has is one line ("units#13: only here").
    /// </summary>
    public static List<string> Compare(string a, string b, int max = 12)
    {
        var mine = Parse(a);
        var theirs = Parse(b);
        var found = new List<string>();
        var missing = new HashSet<string>();
        void Only(string key, string where, OrderedLookup other)
        {
            string thing = Thing(key);
            if (!other.HasThing(thing)) { if (missing.Add(thing)) found.Add($"{thing}: only {where}"); }
            else found.Add($"{key}: only {where}");
        }
        foreach (var (key, value) in mine)
        {
            if (found.Count >= max) break;
            if (!theirs.TryGetValue(key, out var other)) Only(key, "here", theirs);
            else if (other != value) found.Add($"{key}: {value} here, {other} there");
        }
        foreach (var (key, _) in theirs)
        {
            if (found.Count >= max) break;
            if (!mine.ContainsKey(key)) Only(key, "there", mine);
        }
        return found;
    }

    // "units#13.Position" -> "units#13"; "roads#2.pieces#4.Health" -> "roads#2.pieces#4".
    static string Thing(string key) => key.LastIndexOf('.') is int dot and > 0 ? key[..dot] : key;

    // In order, so the first differences come first.
    static List<KeyValuePair<string, string>> ParseList(string dump) =>
        [.. dump.Split('\n').Where(l => l.Contains(" = ")).Select(l => { int i = l.IndexOf(" = ", StringComparison.Ordinal); return KeyValuePair.Create(l[..i], l[(i + 3)..]); })];

    static OrderedLookup Parse(string dump) => new(ParseList(dump));

    sealed class OrderedLookup(List<KeyValuePair<string, string>> lines) : IEnumerable<KeyValuePair<string, string>>
    {
        readonly Dictionary<string, string> _byKey = lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First().Value);
        public bool TryGetValue(string key, out string value) => _byKey.TryGetValue(key, out value!);
        public bool ContainsKey(string key) => _byKey.ContainsKey(key);
        readonly HashSet<string> _things = [.. lines.Select(l => Thing(l.Key))];
        public bool HasThing(string thing) => _things.Contains(thing);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => lines.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Auto-property backing fields read as their property's name.
    static string Name(FieldInfo f) => f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name;

    static string Format(object? value) => value switch
    {
        null => "null",
        float f => $"{f.ToString("R", CultureInfo.InvariantCulture)}|{BitConverter.SingleToInt32Bits(f):x8}",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Vector3 v => $"({Format(v.X)}, {Format(v.Y)}, {Format(v.Z)})",
        string s => '"' + s + '"',
        bool or int or uint or long or Enum => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        UnitType t => t.Id,
        BuildingType t => t.Id,
        BezierSegment c => $"{Format(c.P0)} {Format(c.P1)} {Format(c.P2)} {Format(c.P3)} length {Format(c.Length)}",
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]",
        _ => "{" + string.Join(", ", value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
                .Select(p => $"{p.Name} {Format(p.GetValue(value))}")) + "}",
    };
}
