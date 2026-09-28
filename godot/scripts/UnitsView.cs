using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// One scene instance per unit (with its selection ring), a ground line through each unit's current
/// and queued orders, and a flickering line from each firing unit to its target.
/// </summary>
public partial class UnitsView : Node3D
{
    const float PathHeight = 0.05f;  // just above the ground
    const float MuzzleHeight = 0.5f; // unit cube center
    const float TargetHeight = 0.2f; // belt surface

    [Export] public PackedScene UnitScene = null!;
    [Export] public Material? PathMaterial;
    [Export] public Material? FireMaterial;

    readonly Dictionary<int, UnitView> _views = [];
    readonly Dictionary<int, Vector3> _positions = []; // interpolated, this frame
    readonly ImmediateMesh _lines = new();

    public override void _Ready() =>
        AddChild(new MeshInstance3D { Mesh = _lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

    public void Sync(Simulation sim, float alpha, IReadOnlyList<int> selection)
    {
        _lines.ClearSurfaces();
        var state = sim.State;
        var positions = _positions;
        positions.Clear();
        foreach (var unit in state.Units)
        {
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = UnitScene.Instantiate<UnitView>();
                AddChild(view);
                _views[unit.Id] = view;
            }
            view.GlobalPosition = positions[unit.Id] = ToGodot(unit.PrevPosition).Lerp(ToGodot(unit.Position), alpha);
            view.Selected = selection.Contains(unit.Id);
        }

        DrawOrderPaths(sim, positions);
        DrawFire(state, positions);
    }

    // Current target, then each queued order's point; a queued segment order's point depends on where the leg before it ends.
    void DrawOrderPaths(Simulation sim, Dictionary<int, Vector3> positions)
    {
        bool drawing = false;
        foreach (var unit in sim.State.Units)
        {
            if (unit.Current.Kind == UnitOrder.None) continue;
            if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, PathMaterial); drawing = true; }

            var start = positions[unit.Id];
            var target = unit.Current.Target;
            Leg(ref start, ToGodot(target));
            foreach (var order in unit.Pending)
            {
                target = sim.OrderPoint(order, target);
                Leg(ref start, ToGodot(target));
            }
        }
        if (drawing) _lines.SurfaceEnd();
    }

    void DrawFire(SimState state, Dictionary<int, Vector3> positions)
    {
        if (state.Tick % 4 >= 2) return; // bursts, not a solid beam
        bool drawing = false;
        foreach (var unit in state.Units)
        {
            if (!unit.Firing) continue;
            if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, FireMaterial); drawing = true; }
            _lines.SurfaceAddVertex(positions[unit.Id] + new Vector3(0, MuzzleHeight, 0));
            _lines.SurfaceAddVertex(ToGodot(unit.Current.Target) with { Y = TargetHeight });
        }
        if (drawing) _lines.SurfaceEnd();
    }

    // Draws start -> end on the ground and advances start to end.
    void Leg(ref Vector3 start, Vector3 end)
    {
        var lift = new Vector3(0, PathHeight, 0);
        _lines.SurfaceAddVertex(start with { Y = 0 } + lift);
        _lines.SurfaceAddVertex(end with { Y = 0 } + lift);
        start = end;
    }
}
