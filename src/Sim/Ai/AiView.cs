using System.Numerics;

namespace Sim.Ai;

/// <summary>
/// What an AI player may know: its side's sight and memory under fog of war (Simulation's Vision), the
/// same a human player gets. The commander asks this before it acts on anything of an enemy's: enemy
/// units only while seen, enemy buildings and posts as remembered, belt as last seen.
/// </summary>
public sealed class AiView(Simulation sim, int player)
{
    public int Player { get; } = player;

    public SimState State => sim.State;

    /// <summary>Whether fog of war is on, so there's anything worth scouting.</summary>
    public bool Fogged => sim.FogOfWar && sim.Nav is not null;

    /// <summary>Whether the player sees this point now.</summary>
    public bool Sees(Vector3 at) => sim.Sees(Player, at);

    /// <summary>Whether the player sees a truck now: trucks are nobody's, so only while in sight.</summary>
    public bool SeesTruck(in Package truck) => sim.SeesArea(Player, truck.Position, Simulation.TruckRadius);

    /// <summary>Whether the player sees this unit now (its own always).</summary>
    public bool SeesUnit(in Unit unit) => sim.SeesUnit(Player, unit);

    /// <summary>The enemy buildings and posts it remembers, as last seen.</summary>
    public List<Ghost> Ghosts => sim.Vision(Player).Ghosts;

    /// <summary>The tick it last saw a point; int.MinValue if never.</summary>
    public int LastSeen(Vector3 at) => sim.LastSeen(Player, at);

    /// <summary>A belt segment as it last saw it.</summary>
    public SegmentState SeenState(int line, int segment) => sim.SeenState(Player, line, segment);
}
