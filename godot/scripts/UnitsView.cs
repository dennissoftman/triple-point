using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>One scene instance per unit, plus a ground line through each unit's current and queued orders.</summary>
public partial class UnitsView : Node3D
{
    const float PathHeight = 0.05f; // just above the ground

    [Export] public PackedScene UnitScene = null!;
    [Export] public Material? PathMaterial;

    readonly Dictionary<int, Node3D> _views = [];
    readonly ImmediateMesh _paths = new();

    public override void _Ready() =>
        AddChild(new MeshInstance3D
        {
            Mesh = _paths,
            MaterialOverride = PathMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

    public void Sync(Simulation sim, float alpha)
    {
        _paths.ClearSurfaces();
        bool drawing = false;
        foreach (var unit in sim.State.Units)
        {
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = UnitScene.Instantiate<Node3D>();
                AddChild(view);
                _views[unit.Id] = view;
            }
            var position = ToGodot(unit.PrevPosition).Lerp(ToGodot(unit.Position), alpha);
            view.GlobalPosition = position;

            if (unit.Current.Kind == UnitOrder.None) continue;
            if (!drawing) { _paths.SurfaceBegin(Mesh.PrimitiveType.Lines); drawing = true; }

            // Current target, then each queued order's point; a queued repair's point depends on where the leg before it ends.
            var from = position;
            var target = unit.Current.Target;
            Leg(ref from, ToGodot(target));
            foreach (var order in unit.Pending)
            {
                target = order.Kind == UnitOrder.Repair ? sim.RepairPoint(order.Line, order.Segment, target) : order.Target;
                Leg(ref from, ToGodot(target));
            }
        }
        if (drawing) _paths.SurfaceEnd();
    }

    // Draws start -> end on the ground and advances start to end.
    void Leg(ref Vector3 start, Vector3 end)
    {
        var lift = new Vector3(0, PathHeight, 0);
        _paths.SurfaceAddVertex(start with { Y = 0 } + lift);
        _paths.SurfaceAddVertex(end with { Y = 0 } + lift);
        start = end;
    }
}
