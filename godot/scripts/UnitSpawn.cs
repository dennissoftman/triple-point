using Godot;

/// <summary>A map marker where a player's unit starts. UnitType is an id from /data/units.json.</summary>
public partial class UnitSpawn : OwnedMarker
{
    [Export] public string UnitType = "rifle_squad";
}
