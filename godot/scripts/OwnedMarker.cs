using Godot;

/// <summary>A map marker for something a player owns from the start, such as a gatherer post.</summary>
public partial class OwnedMarker : Marker3D
{
    [Export] public int Player; // sim player index
}
