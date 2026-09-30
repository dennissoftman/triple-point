using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Sim;

/// <summary>
/// Writes a match's numbers (MatchStats) as a plain text file for playtests: who won and when, totals per
/// side, the order things were first built and trained, and a line every 30 s. For the developer, not
/// the player, so it isn't localized (only the side names are, as the game shows them).
/// </summary>
public static class MatchReport
{
    const string Folder = "user://matches";

    /// <summary>Writes the report and returns its full path, or null if it couldn't.</summary>
    public static string? Write(MatchStats stats, SimState state, string scene, string ai)
    {
        var text = Format(stats, state, scene, ai);
        try
        {
            var folder = ProjectSettings.GlobalizePath(Folder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"match-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Couldn't write the match report: {e.Message}");
            return null;
        }
    }

    static string Time(int tick)
    {
        int s = tick / Simulation.TicksPerSecond;
        return $"{s / 60}:{s % 60:00}";
    }

    public static string Format(MatchStats stats, SimState state, string scene, string ai)
    {
        int n = stats.Sides.Count;
        string Name(int p) => PlayerPalette.Name(p);
        var sb = new StringBuilder();
        sb.AppendLine($"Match report, {DateTime.Now:yyyy-MM-dd HH:mm}, {scene}");
        sb.AppendLine($"AI: {ai}");
        sb.AppendLine(stats.Winner != Player.None ? $"{Name(stats.Winner)} won at {Time(stats.EndTick)}"
            : state.GameOver ? $"A draw at {Time(stats.EndTick)}" : $"Stopped at {Time(stats.EndTick)}, no winner yet");
        sb.AppendLine();

        void Row(string label, Func<MatchStats.Side, int, string> cell)
        {
            sb.Append(label.PadRight(34));
            for (int p = 0; p < n; p++) sb.Append(cell(stats.Sides[p], p).PadRight(14));
            sb.AppendLine();
        }
        Row("", (_, p) => Name(p));
        Row("Packages gathered at posts", (_, p) => $"{state.Players[p].Gathered}");
        Row("Packages picked up (spill)", (_, p) => $"{state.Players[p].Collected}");
        Row("Packages spent", (_, p) => $"{state.Players[p].Spent}");
        Row("Posts built / lost", (s, _) => $"{s.PostsBuilt} / {s.PostsLost}");
        Row("Buildings built / lost", (s, _) => $"{s.BuildingsBuilt} / {s.BuildingsLost}");
        Row("Units trained", (s, _) => $"{s.Trained.Values.Sum()}");
        Row("Units lost (their cost)", (s, _) => $"{s.UnitsLost} ({s.ValueLost})");
        Row("Belt segments it broke", (s, _) => $"{s.Breaks}");
        Row("  of those, cutting enemy posts", (s, _) => $"{s.BreaksCuttingEnemy}");
        Row("  of those, cutting its own posts", (s, _) => $"{s.BreaksCuttingOwn}");
        Row("Times its posts were cut off", (s, _) => $"{s.CutOff}");
        Row("Repairs upstream of its posts", (s, _) => $"{s.Repaired}");
        sb.AppendLine($"Packages lost at belt ends (no post took them): {stats.PackagesLostAtEnds}");
        sb.AppendLine();

        sb.AppendLine("Trained");
        for (int p = 0; p < n; p++)
            sb.AppendLine($"  {Name(p),-10} {string.Join(", ", stats.Sides[p].Trained.Select(kv => $"{kv.Key} x{kv.Value}"))}");
        sb.AppendLine();

        sb.AppendLine("Firsts (building finished, or unit type trained)");
        foreach (var f in stats.Firsts)
            sb.AppendLine($"  {Time(f.Tick),6}  {Name(f.Player),-10} {(f.Unit ? "trained" : "built")} {f.Type}");
        sb.AppendLine();

        sb.AppendLine($"Every {MatchStats.SampleSeconds:0} s: packages in hand, income since the last line (posts + pickups), posts, fighters (their cost)");
        sb.Append("  time  ");
        for (int p = 0; p < n; p++) sb.Append(Name(p).PadRight(30));
        sb.AppendLine();
        int rows = stats.Sides.Max(s => s.Samples.Count);
        for (int i = 0; i < rows; i++)
        {
            sb.Append($"  {Time(stats.Sides.First(s => s.Samples.Count > i).Samples[i].Tick),5} ");
            for (int p = 0; p < n; p++)
            {
                var samples = stats.Sides[p].Samples;
                if (i >= samples.Count) { sb.Append("".PadRight(30)); continue; }
                var s = samples[i];
                int income = s.Gathered + s.Collected - (i > 0 ? samples[i - 1].Gathered + samples[i - 1].Collected : 0);
                sb.Append($"{s.Packages,4} +{income,-4} {s.Posts,2} posts {s.Fighters,3} ({s.ArmyValue})".PadRight(30));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
