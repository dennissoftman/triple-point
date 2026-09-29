using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Turns player input into selection and commands for the local player, and shows what a click would do
/// as the cursor. Selection is local UI state: the sim never sees it, only the commands issued to the
/// selected units, or to the one selected building (production, rally point). With builders selected, a
/// build slot arms placement: a ghost follows the cursor until a click puts the building down. Input goes
/// through Input Map actions (Project Settings), never literal keys.
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
    const float PostSnapRadius = 6f;      // m from the cursor to a belt that a post being placed snaps to

    [Export] public SimHost Host = null!;
    [Export] public RtsCamera Camera = null!;
    [Export] public BeltView BeltView = null!;
    [Export] public Control SelectionBox = null!;
    [Export] public int LocalPlayer;                        // the player you control; debug_swap_player cycles it
    [Export] public string CursorName = nameof(CursorKind.Default); // what the cursor shows; for tools
    [Export] public int SelectedBuilding = -1;              // a building id, or -1; selected alone, never with units

    /// <summary>The command card's slots, in order: a building's unit types, or what builders can build.</summary>
    public static readonly string[] SlotActions = ["slot_1", "slot_2", "slot_3", "slot_4"];

    /// <summary>What a click will order: the cursor and the commands both come from this.</summary>
    enum Act { None, Move, Attack, AttackMove, Capture, AttackSegment, Repair, Rally, Resume }
    readonly record struct Intent(Act Kind, SVector3 Point, int Target = -1, int Line = -1, int Segment = -1);

    /// <summary>
    /// A building about to be placed: snapped to the grid under the cursor (a post: beside the nearest
    /// belt), and what's wrong with putting it there, if anything.
    /// </summary>
    public readonly record struct Placement(BuildingType Type, SVector3 At, float Heading, string? Problem)
    {
        public bool Valid => Problem is null;
    }

    public IReadOnlyList<int> Selection => _selection;

    /// <summary>The building being placed, while placement is armed and the cursor is over the ground.</summary>
    public Placement? Placing { get; private set; }

    readonly List<int> _selection = []; // unit ids, in selection order (sets formation slots)
    readonly Dictionary<int, CameraView> _views = []; // each side's camera, saved when you swap away
    Vector2? _dragStart;
    Vector2 _mouse;        // where the mouse is, from its own events
    bool _attackMoveArmed; // by attack_move: the next left click attack-moves
    bool _skipRelease;     // the release after a double-click isn't a click of its own
    string? _placingType;  // a building type id, while placement is armed
    float _placingHeading; // radians, in 90° steps
    CursorKind _cursor = CursorKind.Default;

    public override void _Process(double delta)
    {
        // Drop units that died.
        for (int i = _selection.Count - 1; i >= 0; i--)
            if (!Host.Sim.TryGetTarget(_selection[i], out _, out _)) _selection.RemoveAt(i);
        if (_selection.Count == 0) _attackMoveArmed = false;
        if (Building is null) SelectedBuilding = -1;
        UpdatePlacement();
        UpdateCursor(_mouse); // every frame: units move under a still mouse too
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse mouse) _mouse = mouse.Position;
    }

    public override void _ExitTree() => Cursors.Release();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("debug_swap_player")) { SwapPlayer(); return; }
        if (e.IsActionPressed("attack_move")) { _attackMoveArmed = _selection.Count > 0; return; }
        if (e.IsActionPressed("cancel")) { (_attackMoveArmed, _placingType) = (false, null); return; }
        if (Building is Building building && ProductionKey(e, building)) return;
        if (BuildKey(e)) return;
        if (e is not InputEventMouse mouse) return;

        if (_placingType is not null)
        {
            if (e.IsActionPressed("select")) Place(Input.IsActionPressed("queue_order"));
            else if (e.IsActionPressed("act")) _placingType = null; // right click cancels
            if (e is InputEventMouseButton) return;
        }

        if (_attackMoveArmed)
        {
            bool queued = Input.IsActionPressed("queue_order");
            if (e.IsActionPressed("select") && Camera.GroundPoint(mouse.Position) is Vector3 target)
            {
                Issue(AttackMoveIntent(mouse.Position, ToSim(target)), queued);
                _attackMoveArmed = queued; // shift keeps it armed, for a chain of waypoints
            }
            else if (e.IsActionPressed("act")) _attackMoveArmed = false; // right click cancels
            return;
        }

        if (e.IsActionPressed("select") && e is InputEventMouseButton { DoubleClick: true })
        {
            SelectSameType(mouse.Position);
            _skipRelease = true;
        }
        else if (e.IsActionPressed("select")) _dragStart = mouse.Position;
        else if (e is InputEventMouseMotion && _dragStart is Vector2 start) ShowBox(start, mouse.Position);
        else if (e.IsActionReleased("select"))
        {
            if (_skipRelease) _skipRelease = false;
            else if (_dragStart is Vector2 from)
            {
                _dragStart = null;
                SelectionBox.Visible = false;
                Select(from, mouse.Position);
            }
        }
        else if (e.IsActionPressed("act") && Camera.GroundPoint(mouse.Position) is Vector3 ground)
            Issue(RightClickIntent(mouse.Position, ToSim(ground)), Input.IsActionPressed("queue_order"));
    }

    // ---- Production ----

    /// <summary>The selected building, if it's still there.</summary>
    public Building? Building => SelectedBuilding < 0 ? null : Host.Sim.State.Buildings.Find(b => b.Id == SelectedBuilding);

    /// <summary>Queues a unit of this type at the selected building.</summary>
    public void Produce(string unitType)
    {
        if (Building is Building b) Host.Issue(new ProduceCommand(LocalPlayer, b.Id, unitType));
    }

    /// <summary>Takes the last queued unit of this type (any type, if null) off the selected building's queue.</summary>
    public void CancelLast(string? unitType)
    {
        if (Building is not Building b) return;
        int index = b.Queue.FindLastIndex(t => unitType is null || t.Id == unitType);
        if (index >= 0) Host.Issue(new CancelProductionCommand(LocalPlayer, b.Id, index));
    }

    public void ToggleRepeat()
    {
        if (Building is Building b) Host.Issue(new SetRepeatCommand(LocalPlayer, b.Id, !b.Repeat));
    }

    // The production keys, while a building is selected: one per unit type it produces, repeat, and
    // cancel the last queued.
    bool ProductionKey(InputEvent e, Building building)
    {
        var types = building.Type.Units;
        for (int i = 0; i < SlotActions.Length && i < types.Length; i++)
            if (e.IsActionPressed(SlotActions[i])) { Produce(types[i].Id); return true; }
        if (e.IsActionPressed("produce_repeat")) { ToggleRepeat(); return true; }
        if (e.IsActionPressed("cancel_production")) { CancelLast(null); return true; }
        return false;
    }

    // ---- Construction ----

    /// <summary>What the selected builders can build (the first builder's list), in slot order; empty without one.</summary>
    public IReadOnlyList<BuildingType> Buildable
    {
        get
        {
            foreach (int id in _selection)
            {
                var unit = Host.Sim.State.Units.Find(u => u.Id == id);
                if (unit.Builds is null) continue;
                var types = new List<BuildingType>();
                foreach (string b in unit.Builds)
                    if (Host.Sim.BuildingTypes.TryGetValue(b, out var type)) types.Add(type);
                return types;
            }
            return [];
        }
    }

    /// <summary>The building type being placed, if placement is armed.</summary>
    public string? PlacingType => _placingType;

    /// <summary>Arms placement of a building type, facing the map's middle (the nearest 90° step).</summary>
    public void StartPlacement(string buildingType)
    {
        _attackMoveArmed = false;
        _placingType = buildingType;
        var from = Host.HomeOf(LocalPlayer);
        _placingHeading = MathF.Round(MathF.Atan2(-from.X, -from.Y) / (MathF.PI / 2)) * (MathF.PI / 2);
    }

    // The build keys, while builders are selected: one per building type, and rotating the one being placed.
    bool BuildKey(InputEvent e)
    {
        if (_placingType is not null && e.IsActionPressed("rotate_building"))
        {
            _placingHeading = (_placingHeading + MathF.PI / 2) % MathF.Tau;
            return true;
        }
        var types = Buildable;
        for (int i = 0; i < SlotActions.Length && i < types.Count; i++)
            if (e.IsActionPressed(SlotActions[i])) { StartPlacement(types[i].Id); return true; }
        return false;
    }

    // Where the armed building would go: under the cursor, snapped to the grid, or for a post beside the
    // nearest belt, facing it; and whether it can: the spot clear, and the whole cost in hand. Placement
    // ends when no builder is selected any more.
    void UpdatePlacement()
    {
        Placing = null;
        if (_placingType is null) return;
        if (!Host.Sim.BuildingTypes.TryGetValue(_placingType, out var type) || Buildable.Count == 0)
        {
            _placingType = null;
            return;
        }
        if (Camera.GroundPoint(_mouse) is not Vector3 ground) return;
        var point = ToSim(ground) with { Y = 0 };
        var (at, heading, problem) = (Simulation.SnapToGrid(point, type.Size), _placingHeading, (string?)null);
        if (type.Kind == BuildingKind.Post && !Host.Sim.SnapToBelt(point, PostSnapRadius, out at, out heading))
            (at, problem) = (point, "must go beside a belt");
        if (problem is null && !Host.Sim.CanPlace(type, at)) problem = "something's in the way";
        if (problem is null && !Host.Sim.CanAfford(LocalPlayer, type)) problem = $"not enough Resources (needs {type.Cost})";
        Placing = new Placement(type, at, heading, problem);
    }

    // Sends the nearest selected builder to put the building down (queued: after what it's doing, and
    // placement stays armed for the next one).
    void Place(bool queued)
    {
        if (Placing is not { Valid: true } placing) return;
        int builder = -1;
        float best = float.MaxValue;
        foreach (var unit in Host.Sim.State.Units)
        {
            if (unit.Builds is null || Array.IndexOf(unit.Builds, placing.Type.Id) < 0 || !_selection.Contains(unit.Id)) continue;
            float d = SVector3.DistanceSquared(unit.Position, placing.At);
            if (d < best) (best, builder) = (d, unit.Id);
        }
        if (builder < 0) return;
        Host.Issue(new BuildCommand(LocalPlayer, builder, placing.Type.Id, placing.At, placing.Heading, queued));
        if (!queued) _placingType = null;
    }

    /// <summary>Orders the selection to move to a ground point, in formation (the minimap's right click).</summary>
    public void MoveSelectionTo(SVector3 point, bool queued) => Issue(new Intent(Act.Move, point), queued);

    // ---- What a click would do ----

    // What a right-click here orders the selection to do: attack an enemy, hold a switch (capturing is
    // automatic), force-attack a segment, repair a damaged one, otherwise move. With a building
    // selected, it sets the rally point.
    Intent RightClickIntent(Vector2 screen, SVector3 point)
    {
        if (SelectedBuilding >= 0) return new(Act.Rally, point);
        if (_selection.Count == 0) return default;
        var sim = Host.Sim;
        if (Buildable.Count > 0 && BuildingAt(point, mine: true) is int site && sim.State.Buildings.Find(b => b.Id == site) is { Built: false })
            return new(Act.Resume, point, site);
        if (EnemyAt(screen, point) is int target) return new(Act.Attack, point, target);
        if (sim.FindJunction(point, JunctionPickRadius, out int j) && sim.State.Junctions[j].IsSwitch)
            return new(Act.Capture, sim.State.Junctions[j].Position with { Y = 0 });
        if (sim.FindSegment(point, BeltView.BeltWidth / 2 + PickTolerance, out int line, out int segment))
        {
            if (Input.IsActionPressed("force_attack")) return new(Act.AttackSegment, point, Line: line, Segment: segment);
            var s = sim.State.Belts[line].Segments[segment];
            if (s.Health < s.MaxHealth) return new(Act.Repair, point, Line: line, Segment: segment);
        }
        return new(Act.Move, point);
    }

    // Once attack-move is armed, a left click on an enemy attacks it; anywhere else attack-moves there.
    Intent AttackMoveIntent(Vector2 screen, SVector3 point) =>
        EnemyAt(screen, point) is int target ? new(Act.Attack, point, target) : new(Act.AttackMove, point);

    void Issue(Intent intent, bool queued)
    {
        if (intent.Kind == Act.Rally)
        {
            Host.Issue(new SetRallyCommand(LocalPlayer, SelectedBuilding, intent.Point));
            return;
        }
        for (int i = 0; i < _selection.Count; i++)
        {
            int id = _selection[i];
            var spread = intent.Point + FormationOffset(i, _selection.Count); // a grid, so the group doesn't stack
            bool builder = Host.Sim.State.Units.Find(u => u.Id == id).Builds is not null;
            Command? command = intent.Kind switch
            {
                Act.Resume when builder => new ResumeBuildCommand(LocalPlayer, id, intent.Target, queued),
                Act.Resume => new MoveCommand(LocalPlayer, id, spread, queued),
                Act.Move or Act.Capture => new MoveCommand(LocalPlayer, id, spread, queued),
                Act.AttackMove => new AttackMoveCommand(LocalPlayer, id, spread, queued),
                Act.Attack => new AttackCommand(LocalPlayer, id, intent.Target, queued),
                Act.AttackSegment => new AttackSegmentCommand(LocalPlayer, id, intent.Line, intent.Segment, queued),
                Act.Repair => new RepairSegmentCommand(LocalPlayer, id, intent.Line, intent.Segment, queued),
                _ => null,
            };
            if (command is not null) Host.Issue(command);
        }
    }

    // The cursor says what a click would do right now: over your switch a left click flips it, over one
    // of your units it selects it, and otherwise it's whatever a right-click would order.
    void UpdateCursor(Vector2 screen)
    {
        var kind = CursorKind.Default; // also while placing a building: the ghost shows what a click does
        if (_placingType is null && Camera.GroundPoint(screen) is Vector3 ground)
        {
            var point = ToSim(ground);
            if (_attackMoveArmed)
                kind = AttackMoveIntent(screen, point).Kind == Act.Attack ? CursorKind.Attack : CursorKind.AttackMove;
            else if (OwnSwitchAt(point) >= 0) kind = CursorKind.Flip;
            else if (UnitAt(screen, mine: true) is null)
                kind = RightClickIntent(screen, point).Kind switch
                {
                    Act.Move or Act.Rally => CursorKind.Move,
                    Act.Attack or Act.AttackSegment => CursorKind.Attack,
                    Act.Capture => CursorKind.Capture,
                    Act.Repair or Act.Resume => CursorKind.Repair,
                    _ => CursorKind.Default,
                };
        }
        if (kind == _cursor) return;
        _cursor = kind;
        CursorName = kind.ToString();
        Cursors.Apply(kind);
    }

    // ---- Selection ----

    // Hotseat for testing both sides before the AI exists. Each side keeps its own camera: leaving saves
    // where you were looking, and coming back glides there (or to the side's spawn on its first turn).
    void SwapPlayer()
    {
        _views[LocalPlayer] = Camera.View;
        LocalPlayer = (LocalPlayer + 1) % Host.Sim.State.Players.Count;
        _selection.Clear();
        SelectedBuilding = -1;
        (_attackMoveArmed, _placingType) = (false, null);
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
    // otherwise a click picks one of your units (select_add toggles it), or else one of your buildings,
    // alone. A drag picks all your units in the box (select_add adds). Clicking empty ground without
    // select_add clears.
    void Select(Vector2 from, Vector2 to)
    {
        bool add = Input.IsActionPressed("select_add");
        bool click = from.DistanceTo(to) < DragThreshold;
        if (click && FlipSwitchAt(to)) return;

        int? unit = click ? UnitAt(to, mine: true) : null;
        if (click && unit is null && Camera.GroundPoint(to) is Vector3 ground && BuildingAt(ToSim(ground), mine: true) is int building)
        {
            _selection.Clear();
            SelectedBuilding = building;
            return;
        }
        SelectedBuilding = -1;
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

    // Double-clicking one of your units picks every unit of its type on screen (select_add adds them).
    void SelectSameType(Vector2 screen)
    {
        if (UnitAt(screen, mine: true) is not int clicked) return;
        SelectedBuilding = -1;
        string type = Host.Sim.State.Units.Find(u => u.Id == clicked).Type;
        if (!Input.IsActionPressed("select_add")) _selection.Clear();
        var onScreen = GetViewport().GetVisibleRect();
        foreach (var u in Host.Sim.State.Units)
            if (u.Owner == LocalPlayer && u.Type == type && ScreenPosition(u) is Vector2 p && onScreen.HasPoint(p) && !_selection.Contains(u.Id))
                _selection.Add(u.Id);
    }

    // Clicking a switch you own sets it to its next output; the owner can do this from anywhere.
    bool FlipSwitchAt(Vector2 screen)
    {
        if (Camera.GroundPoint(screen) is not Vector3 ground || OwnSwitchAt(ToSim(ground)) is not (>= 0 and var j)) return false;
        var junction = Host.Sim.State.Junctions[j];
        Host.Issue(new SetJunctionCommand(LocalPlayer, j, (junction.Selected + 1) % junction.Outputs.Count));
        return true;
    }

    // The switch you own whose disc is under this ground point, or -1.
    int OwnSwitchAt(SVector3 point)
    {
        var sim = Host.Sim;
        if (!sim.FindJunction(point, SwitchDiscRadius, out int j)) return -1;
        var junction = sim.State.Junctions[j];
        return junction.IsSwitch && junction.Owner == LocalPlayer ? j : -1;
    }

    // ---- Picking ----

    int? EnemyAt(Vector2 screen, SVector3 ground)
    {
        if (UnitAt(screen, mine: false) is int unit) return unit;
        foreach (var post in Host.Sim.State.Gatherers)
        {
            float dx = post.Position.X - ground.X, dz = post.Position.Z - ground.Z;
            if (post.Owner != LocalPlayer && dx * dx + dz * dz <= PostPickRadius * PostPickRadius) return post.Id;
        }
        return BuildingAt(ground, mine: false);
    }

    // The building (yours, or anyone else's) whose footprint covers this ground point.
    int? BuildingAt(SVector3 ground, bool mine)
    {
        foreach (var b in Host.Sim.State.Buildings)
        {
            float half = b.Type.Size / 2;
            if ((b.Owner == LocalPlayer) == mine && MathF.Abs(b.Position.X - ground.X) <= half && MathF.Abs(b.Position.Z - ground.Z) <= half)
                return b.Id;
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
