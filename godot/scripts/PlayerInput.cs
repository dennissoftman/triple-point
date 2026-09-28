using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Turns player input into selection and commands for the local player. Selection is local UI state:
/// the sim never sees it, only the commands issued to the selected units.
/// Input goes through Input Map actions (Project Settings), never literal keys.
/// </summary>
public partial class PlayerInput : Node
{
    const float DragThreshold = 6f;       // px before a click becomes a box
    const float VehiclePickRadius = 30f;  // px around a unit's screen position
    const float SquadPickRadius = 44f;    // squads are spread out
    const float UnitCenterHeight = 0.6f;
    const float PostPickRadius = 1.3f;    // m on the ground
    const float JunctionPickRadius = 1.5f; // m; right-clicking this close to a switch holds it
    const float SwitchDiscRadius = 1.1f;   // m; left-clicking the disc of your switch flips it
    const float PickTolerance = 0.5f;     // m beyond the belt edge that still counts as clicking it
    const float FormationSpacing = 3f;    // m between units of a group move

    [Export] public SimHost Host = null!;
    [Export] public RtsCamera Camera = null!;
    [Export] public BeltView BeltView = null!;
    [Export] public Control SelectionBox = null!;
    [Export] public int LocalPlayer; // the player you control; debug_swap_player cycles it

    public IReadOnlyList<int> Selection => _selection;

    readonly List<int> _selection = []; // unit ids, in selection order (sets formation slots)
    readonly Dictionary<int, CameraView> _views = []; // each side's camera, saved when you swap away
    Vector2? _dragStart;

    public override void _Process(double delta)
    {
        // Drop units that died.
        for (int i = _selection.Count - 1; i >= 0; i--)
            if (!Host.Sim.TryGetTarget(_selection[i], out _, out _)) _selection.RemoveAt(i);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("debug_swap_player"))
        {
            SwapPlayer();
            return;
        }
        if (e is not InputEventMouse mouse) return;

        if (e.IsActionPressed("select")) _dragStart = mouse.Position;
        else if (e is InputEventMouseMotion && _dragStart is Vector2 start) ShowBox(start, mouse.Position);
        else if (e.IsActionReleased("select") && _dragStart is Vector2 from)
        {
            _dragStart = null;
            SelectionBox.Visible = false;
            Select(from, mouse.Position);
        }
        else if (e.IsActionPressed("act") && Camera.GroundPoint(mouse.Position) is Vector3 ground)
            Act(mouse.Position, ToSim(ground));
    }

    // Hotseat for testing both sides before the AI exists. Each side keeps its own camera: leaving saves
    // where you were looking, and coming back glides there (or to the side's spawn on its first turn).
    void SwapPlayer()
    {
        _views[LocalPlayer] = Camera.View;
        LocalPlayer = (LocalPlayer + 1) % Host.Sim.State.Players.Count;
        _selection.Clear();
        Camera.FlyTo(_views.TryGetValue(LocalPlayer, out var last) ? last : new CameraView(Host.HomeOf(LocalPlayer), Camera.HomeDistance));
    }

    void ShowBox(Vector2 from, Vector2 to)
    {
        if (from.DistanceTo(to) < DragThreshold) return;
        var rect = new Rect2(from, to - from).Abs();
        SelectionBox.Position = rect.Position;
        SelectionBox.Size = rect.Size;
        SelectionBox.Visible = true;
    }

    // A click on the disc of a switch you own flips it (even with your units standing around it);
    // otherwise a click picks one of your units (select_add toggles it). A drag picks all your units in
    // the box (select_add adds). Clicking empty ground without select_add clears.
    void Select(Vector2 from, Vector2 to)
    {
        bool add = Input.IsActionPressed("select_add");
        bool click = from.DistanceTo(to) < DragThreshold;
        if (click && FlipSwitchAt(to)) return;

        int? unit = click ? UnitAt(to, mine: true) : null;
        if (click && add && unit is int toggled)
        {
            if (!_selection.Remove(toggled)) _selection.Add(toggled);
            return;
        }

        if (!add) _selection.Clear();
        if (click)
        {
            if (unit is int id) _selection.Add(id);
            return;
        }

        var box = new Rect2(from, to - from).Abs();
        foreach (var u in Host.Sim.State.Units)
            if (u.Owner == LocalPlayer && ScreenPosition(u) is Vector2 p && box.HasPoint(p) && !_selection.Contains(u.Id))
                _selection.Add(u.Id);
    }

    // Clicking a switch you own sets it to its next output; the owner can do this from anywhere.
    bool FlipSwitchAt(Vector2 screen)
    {
        if (Camera.GroundPoint(screen) is not Vector3 ground) return false;
        var sim = Host.Sim;
        if (!sim.FindJunction(ToSim(ground), SwitchDiscRadius, out int j)) return false;
        var junction = sim.State.Junctions[j];
        if (!junction.IsSwitch || junction.Owner != LocalPlayer) return false;
        Host.Issue(new SetJunctionCommand(LocalPlayer, j, (junction.Selected + 1) % junction.Outputs.Count));
        return true;
    }

    // The context order at a ground point for every selected unit: attack an enemy, hold a switch
    // (capturing is automatic), force-attack a segment, repair a damaged one, otherwise move.
    void Act(Vector2 screen, SVector3 point)
    {
        if (_selection.Count == 0) return;
        bool queued = Input.IsActionPressed("queue_order");
        var sim = Host.Sim;

        if (EnemyAt(screen, point) is int target)
        {
            foreach (int id in _selection) Host.Issue(new AttackCommand(LocalPlayer, id, target, queued));
            return;
        }
        if (sim.FindJunction(point, JunctionPickRadius, out int j) && sim.State.Junctions[j].IsSwitch)
        {
            MoveGroup(sim.State.Junctions[j].Position with { Y = 0 }, queued);
            return;
        }

        bool onBelt = sim.FindSegment(point, BeltView.BeltWidth / 2 + PickTolerance, out int line, out int segment);
        if (onBelt && Input.IsActionPressed("force_attack"))
            foreach (int id in _selection) Host.Issue(new AttackSegmentCommand(LocalPlayer, id, line, segment, queued));
        else if (onBelt && sim.State.Belts[line].Segments[segment] is { } s && s.Health < s.MaxHealth)
            foreach (int id in _selection) Host.Issue(new RepairSegmentCommand(LocalPlayer, id, line, segment, queued));
        else
            MoveGroup(point, queued);
    }

    // Spread into a grid so the group doesn't stack on one point.
    void MoveGroup(SVector3 point, bool queued)
    {
        for (int i = 0; i < _selection.Count; i++)
            Host.Issue(new MoveCommand(LocalPlayer, _selection[i], point + FormationOffset(i, _selection.Count), queued));
    }

    int? EnemyAt(Vector2 screen, SVector3 ground)
    {
        if (UnitAt(screen, mine: false) is int unit) return unit;
        foreach (var post in Host.Sim.State.Gatherers)
        {
            float dx = post.Position.X - ground.X, dz = post.Position.Z - ground.Z;
            if (post.Owner != LocalPlayer && dx * dx + dz * dz <= PostPickRadius * PostPickRadius) return post.Id;
        }
        return null;
    }

    // The nearest unit (yours, or anyone else's) whose screen position is close to `screen`.
    int? UnitAt(Vector2 screen, bool mine)
    {
        int? best = null;
        float bestDistance = float.MaxValue;
        foreach (var unit in Host.Sim.State.Units)
        {
            if ((unit.Owner == LocalPlayer) != mine || ScreenPosition(unit) is not Vector2 p) continue;
            float d = p.DistanceTo(screen);
            float radius = unit.MaxMembers > 1 ? SquadPickRadius : VehiclePickRadius;
            if (d <= radius && d < bestDistance) (best, bestDistance) = (unit.Id, d);
        }
        return best;
    }

    Vector2? ScreenPosition(in Unit unit)
    {
        var world = ToGodot(unit.Position) + new Vector3(0, UnitCenterHeight, 0);
        return Camera.IsPositionBehind(world) ? null : Camera.UnprojectPosition(world);
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
}
