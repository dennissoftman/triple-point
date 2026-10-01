using System.Collections.Generic;
using Sim;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// What the local player may see, for every view: its side's sight and memory under fog of war. SimHost
/// sets it as it starts and each frame (the local player changes with debug_swap_player), and clears it
/// as it leaves. With `All` (fog off, or a
/// spectator: `--reveal`, the demo) everything shows as it is.
/// </summary>
public static class Sight
{
    public static Simulation? Sim;
    public static int Player;
    public static bool All = true;

    public static bool Sees(in Unit unit) => All || Sim is null || Sim.SeesUnit(Player, unit);

    public static bool SeesAt(SVector3 at) => All || Sim is null || Sim.Sees(Player, at);

    public static bool SeesArea(SVector3 at, float radius) => All || Sim is null || Sim.SeesArea(Player, at, radius);

    /// <summary>A building or post of `owner`'s at `at`: the local player's own always; an enemy's while seen.</summary>
    public static bool SeesStructure(int owner, SVector3 at, float radius) => owner == Player || SeesArea(at, radius);

    /// <summary>How the local player remembers an enemy building or post, if it does.</summary>
    public static bool Remembers(int id, out Ghost ghost)
    {
        ghost = default;
        if (All || Sim is null) return false;
        foreach (var g in Sim.Vision(Player).Ghosts)
            if (g.Id == id) { ghost = g; return true; }
        return false;
    }

    public static IReadOnlyList<Ghost> Ghosts => All || Sim is null ? [] : Sim.Vision(Player).Ghosts;

    public static SegmentState SeenState(SimState state, int line, int segment) =>
        All || Sim is null ? state.Belts[line].Segments[segment].State : Sim.SeenState(Player, line, segment);

    public static float SeenHealth(SimState state, int line, int segment) =>
        All || Sim is null ? state.Belts[line].Segments[segment].Health : Sim.SeenHealth(Player, line, segment);
}
