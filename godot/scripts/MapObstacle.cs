using Godot;

/// <summary>
/// Solid ground on a map, under a map's Obstacles node: rock that nothing crosses or builds on. A box of
/// Size (m) standing on the node's position; its footprint turns with the node about Y. SimHost adds it to
/// the sim, and it draws itself (a placeholder grey block).
/// </summary>
public partial class MapObstacle : Node3D
{
    [Export] public Vector3 Size = new(6, 2.5f, 4);

    static StandardMaterial3D? _material;

    public override void _Ready()
    {
        _material ??= new StandardMaterial3D { AlbedoColor = new Color(0.40f, 0.38f, 0.35f), Roughness = 1 };
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Size },
            MaterialOverride = _material,
            Position = new Vector3(0, Size.Y / 2, 0),
        });
    }
}
