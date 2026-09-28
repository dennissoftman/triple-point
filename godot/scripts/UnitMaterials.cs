using System.Collections.Generic;
using Godot;

/// <summary>
/// Materials shared by all unit views: one body and one turret material per player, and one dark
/// material for wheels, tracks and barrels. Units that share a material can be drawn together; a copy
/// per unit makes each one its own draw setup. Owned by UnitsView, so it goes when the views do.
/// </summary>
public sealed class UnitMaterials
{
    readonly Dictionary<int, (StandardMaterial3D Body, StandardMaterial3D Turret)> _byPlayer = [];

    public readonly StandardMaterial3D Dark = new() { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.9f };

    public (StandardMaterial3D Body, StandardMaterial3D Turret) For(int player)
    {
        if (_byPlayer.TryGetValue(player, out var materials)) return materials;
        var color = PlayerPalette.Color(player);
        return _byPlayer[player] = (
            new StandardMaterial3D { AlbedoColor = color, Roughness = 0.7f },
            new StandardMaterial3D { AlbedoColor = color.Darkened(0.3f) });
    }
}
