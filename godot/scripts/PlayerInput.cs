using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Turns player input into selection and commands. Selection is local UI state: the sim never sees it,
/// only the commands issued to the selected units.
/// Input goes through Input Map actions (Project Settings), never literal keys.
/// </summary>
public partial class PlayerInput : Node
{
    const float DragThreshold = 6f;     // px before a click becomes a box
    const float ClickPickRadius = 28f;  // px around a unit's screen position
    const float UnitCenterHeight = 0.5f;
    const float PickTolerance = 0.5f;   // m beyond the belt edge that still counts as clicking it
    const float FormationSpacing = 2f;  // m between units of a group move

    [Export] public SimHost Host = null!;
    [Export] public Camera3D Camera = null!;
    [Export] public BeltView BeltView = null!;
    [Export] public Control SelectionBox = null!;

    public IReadOnlyList<int> Selection => _selection;

    readonly List<int> _selection = []; // unit ids, in selection order (sets formation slots)
    Vector2? _dragStart;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouse mouse) return;

        if (e.IsActionPressed("select")) _dragStart = mouse.Position;
        else if (e is InputEventMouseMotion && _dragStart is Vector2 start) ShowBox(start, mouse.Position);
        else if (e.IsActionReleased("select") && _dragStart is Vector2 from)
        {
            _dragStart = null;
            SelectionBox.Visible = false;
            Select(from, mouse.Position);
        }
        else if (e.IsActionPressed("act") && GroundPoint(mouse.Position) is SVector3 point) Act(point);
    }

    void ShowBox(Vector2 from, Vector2 to)
    {
        if (from.DistanceTo(to) < DragThreshold) return;
        var rect = new Rect2(from, to - from).Abs();
        SelectionBox.Position = rect.Position;
        SelectionBox.Size = rect.Size;
        SelectionBox.Visible = true;
    }

    // A click picks one unit (select_add toggles it); a drag picks everything in the box (select_add adds).
    // Clicking empty ground without select_add clears the selection.
    void Select(Vector2 from, Vector2 to)
    {
        bool add = Input.IsActionPressed("select_add");
        bool click = from.DistanceTo(to) < DragThreshold;
        if (click && add && UnitAt(to) is int toggled)
        {
            if (!_selection.Remove(toggled)) _selection.Add(toggled);
            return;
        }

        if (!add) _selection.Clear();
        if (click)
        {
            if (UnitAt(to) is int id) _selection.Add(id);
            return;
        }

        var box = new Rect2(from, to - from).Abs();
        foreach (var unit in Host.Sim.State.Units)
            if (ScreenPosition(unit) is Vector2 p && box.HasPoint(p) && !_selection.Contains(unit.Id))
                _selection.Add(unit.Id);
    }

    int? UnitAt(Vector2 screen)
    {
        int? best = null;
        float bestDistance = ClickPickRadius;
        foreach (var unit in Host.Sim.State.Units)
        {
            if (ScreenPosition(unit) is not Vector2 p) continue;
            float d = p.DistanceTo(screen);
            if (d <= bestDistance) (best, bestDistance) = (unit.Id, d);
        }
        return best;
    }

    Vector2? ScreenPosition(in Unit unit)
    {
        var world = ToGodot(unit.Position) + new Vector3(0, UnitCenterHeight, 0);
        return Camera.IsPositionBehind(world) ? null : Camera.UnprojectPosition(world);
    }

    // The context order at a ground point for every selected unit: force-attack a segment,
    // repair a damaged one, otherwise move (spread into a grid so the group doesn't stack on one point).
    void Act(SVector3 point)
    {
        if (_selection.Count == 0) return;
        bool queued = Input.IsActionPressed("queue_order");
        var sim = Host.Sim;
        bool onBelt = sim.FindSegment(point, BeltView.BeltWidth / 2 + PickTolerance, out int line, out int segment);
        bool attack = onBelt && Input.IsActionPressed("force_attack");
        bool repair = onBelt && !attack && sim.State.Belts[line].Segments[segment] is { } s && s.Health < s.MaxHealth;

        for (int i = 0; i < _selection.Count; i++)
        {
            int id = _selection[i];
            Host.Issue(attack ? new AttackSegmentCommand(id, line, segment, queued)
                     : repair ? new RepairSegmentCommand(id, line, segment, queued)
                     : new MoveCommand(id, point + FormationOffset(i, _selection.Count), queued));
        }
    }

    // Slot `index` of a square grid of `count` slots, centered on the target.
    static SVector3 FormationOffset(int index, int count)
    {
        int columns = (int)MathF.Ceiling(MathF.Sqrt(count));
        int rows = (count + columns - 1) / columns;
        float x = index % columns - (columns - 1) / 2f;
        float z = index / columns - (rows - 1) / 2f;
        return new SVector3(x, 0, z) * FormationSpacing;
    }

    SVector3? GroundPoint(Vector2 screen)
    {
        var hit = new Plane(Vector3.Up, 0).IntersectsRay(Camera.ProjectRayOrigin(screen), Camera.ProjectRayNormal(screen));
        return hit is Vector3 p ? ToSim(p) : null;
    }
}
