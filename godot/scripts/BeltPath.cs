using Godot;

/// <summary>
/// A belt line on a map: the curve runs from its source to its end. The first CoveredStart m and the last
/// CoveredEnd m are covered (unbreakable, no posts): where it comes in from beyond the map and runs to
/// where the open belt starts, and where it leaves again. Road up to PavedTo m is paved; the rest dirt.
/// </summary>
public partial class BeltPath : Path3D
{
    [Export] public float CoveredStart; // m
    [Export] public float CoveredEnd;   // m
    [Export] public float PavedTo;      // m
}
