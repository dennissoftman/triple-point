using System.Collections.Generic;
using Godot;

/// <summary>
/// Materials shared by all unit views. Bodies are neutral (khaki infantry, olive vehicles, a darker olive
/// for turrets), the same for every side; a unit's side is its accent, saturated, on what the camera sees
/// from above (helmets, turret tops, cab roofs): silhouette first, color second (doctrine: Look). One dark
/// material for wheels, tracks, barrels and gear. Units that share a material can be drawn together; a copy
/// per unit makes each one its own draw setup. Owned by UnitsView, so it goes when the views do.
/// </summary>
public sealed class UnitMaterials
{
    readonly Dictionary<int, StandardMaterial3D> _accents = [];

    // Lighter than the ground, so units stand out from it in value too (the squint test), not only by hue.
    public readonly StandardMaterial3D Infantry = new() { AlbedoColor = new Color(0.7f, 0.66f, 0.53f), Roughness = 0.9f };
    public readonly StandardMaterial3D Vehicle = new() { AlbedoColor = new Color(0.6f, 0.62f, 0.49f), Roughness = 0.8f };
    public readonly StandardMaterial3D Turret = new() { AlbedoColor = new Color(0.48f, 0.5f, 0.39f), Roughness = 0.8f };
    public readonly StandardMaterial3D Dark = new() { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.9f };

    /// <summary>A side's accent: its color, a little lit so it reads in shade too.</summary>
    public StandardMaterial3D Accent(int player)
    {
        if (_accents.TryGetValue(player, out var material)) return material;
        var color = PlayerPalette.Color(player);
        return _accents[player] = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.6f,
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = 0.25f,
        };
    }
}
