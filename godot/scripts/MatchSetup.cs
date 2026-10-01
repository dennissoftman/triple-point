using Sim.Ai;

/// <summary>
/// What the skirmish setup chose, carried into the match scene and kept for Restart: the side you play,
/// whether the other is the AI (at a level), a second player at the same machine (hotseat) or one over
/// the network (Networked: NetSession's match), and the seed. Null when a scene is run directly (tools,
/// the editor, the command line): then its own exports and the command-line flags decide, with seed 1.
/// </summary>
public static class MatchSetup
{
    public sealed record Choice(int LocalPlayer, bool Hotseat, AiLevel Level, uint Seed = 1, bool Networked = false);

    public static Choice? Current;

    public const string MatchScene = "res://scenes/main.tscn", MenuScene = "res://scenes/menu.tscn";
}
