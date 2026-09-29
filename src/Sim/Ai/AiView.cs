using System.Numerics;

namespace Sim.Ai;

/// <summary>
/// What an AI player may know. The commander asks this before it acts on anything of an enemy's, so fog
/// of war only has to answer <see cref="Sees"/>. Until fog exists it sees everything.
/// </summary>
public sealed class AiView(Simulation sim, int player)
{
    public int Player { get; } = player;

    public SimState State => sim.State;

    /// <summary>Whether the player can see this point now.</summary>
    public bool Sees(Vector3 at) => true;
}
