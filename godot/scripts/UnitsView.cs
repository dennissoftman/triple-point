using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// One scene instance per unit, a ground line through the local player's current and queued orders,
/// and flickering tracer lines from everything that's firing.
/// </summary>
public partial class UnitsView : Node3D
{
    const float PathHeight = 0.05f;  // just above the ground
    const float AimHeight = 0.5f;    // tracers end this far above what they hit

    [Export] public PackedScene UnitScene = null!;
    [Export] public Material? PathMaterial;
    [Export] public Material? FireMaterial;

    readonly Dictionary<int, UnitView> _views = [];
    readonly Dictionary<int, Vector3> _positions = []; // interpolated, this frame
    readonly List<int> _gone = [];
    readonly ImmediateMesh _lines = new();

    public override void _Ready() =>
        AddChild(new MeshInstance3D { Mesh = _lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

    /// <summary>`delta` is the sim time this frame covers, for member movement.</summary>
    public void Sync(Simulation sim, float alpha, float delta, IReadOnlyList<int> selection, int localPlayer)
    {
        _lines.ClearSurfaces();
        _positions.Clear();
        foreach (var unit in sim.State.Units)
        {
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = UnitScene.Instantiate<UnitView>();
                AddChild(view);
                view.Setup(unit.Owner, PlayerPalette.Color(unit.Owner), unit.MaxMembers, unit.Movement);
                _views[unit.Id] = view;
            }
            var position = ToGodot(unit.PrevPosition).Lerp(ToGodot(unit.Position), alpha);
            float heading = Mathf.LerpAngle(unit.PrevHeading, unit.Heading, alpha);
            _positions[unit.Id] = position;
            view.Sync(position, heading, delta, unit.Members, unit.Health / unit.MaxHealth, unit.Firing, ToGodot(unit.FireAt));
            view.Selected = selection.Contains(unit.Id);
        }

        RemoveGone();
        DrawOrderPaths(sim, localPlayer);
        DrawFire(sim.State);
    }

    void RemoveGone()
    {
        if (_views.Count == _positions.Count) return;
        _gone.Clear();
        foreach (int id in _views.Keys)
            if (!_positions.ContainsKey(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            _views[id].QueueFree();
            _views.Remove(id);
        }
    }

    // Only your own side's orders: you don't get to see where the enemy is going.
    // Current target first, then each queued order's point; a queued segment order's point depends on
    // where the leg before it ends.
    void DrawOrderPaths(Simulation sim, int localPlayer)
    {
        bool drawing = false;
        foreach (var unit in sim.State.Units)
        {
            if (unit.Owner != localPlayer || unit.Current.Kind == UnitOrder.None) continue;
            if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, PathMaterial); drawing = true; }

            var start = _positions[unit.Id];
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

    // Short bursts, staggered per member so a squad doesn't fire like one gun.
    void DrawFire(SimState state)
    {
        bool drawing = false;
        foreach (var unit in state.Units)
        {
            if (!unit.Firing) continue;
            var muzzles = _views[unit.Id].Muzzles;
            var at = ToGodot(unit.FireAt) + new Vector3(0, AimHeight, 0);
            for (int i = 0; i < muzzles.Count; i++)
            {
                if ((state.Tick + i) % 4 >= 2) continue;
                if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, FireMaterial); drawing = true; }
                _lines.SurfaceAddVertex(muzzles[i]);
                _lines.SurfaceAddVertex(at);
            }
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
