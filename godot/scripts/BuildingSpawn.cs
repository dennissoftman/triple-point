using Godot;

/// <summary>A map marker where a player's building starts. BuildingType is an id from /data/buildings.json; its exit faces the marker's -Z.</summary>
public partial class BuildingSpawn : OwnedMarker
{
    [Export] public string BuildingType = "hq";
}
