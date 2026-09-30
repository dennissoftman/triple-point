using System.Collections.Generic;
using Godot;
using Sim;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// The minimap, bottom-left: the whole map (the camera's bounds) from above, north up like the main view.
/// Belts (broken segments red) with their packages, posts, buildings
/// (foundations hollow), units as dots (the selection brighter), and the camera's view as an outline.
/// Click or drag to move the camera there; right-click to move the selection there. Under fog of war it
/// shows what the local player sees and remembers (Sight), with unseen ground shaded, and pings where a
/// belt feeding its posts was cut. Drawn with the canvas API each frame, in few calls: every draw call and every
/// array handed to the engine costs, so belts are two batched line lists, rebuilt only when a segment
/// breaks or is repaired. Placeholder look.
/// </summary>
public partial class Minimap : Control
{
    const float Width = 240f;        // px; the height follows the map's shape
    const float Margin = 12f;        // px from the screen's corner
    const float BeltSampleStep = 2f; // m between the points of a belt segment's outline
    static readonly Color Background = new(0.16f, 0.17f, 0.15f, 0.9f), Border = new(0, 0, 0, 0.8f);
    static readonly Color BeltColor = new(0.55f, 0.55f, 0.55f), BrokenColor = new(0.9f, 0.2f, 0.15f), CoveredColor = new(0.33f, 0.34f, 0.35f);
    static readonly Color PackageColor = new(1, 0.62f, 0.1f), ViewColor = new(1, 1, 1, 0.85f);
    static readonly Color ObstacleColor = new(0.36f, 0.34f, 0.31f);

    [Export] public SimHost Host = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public RtsCamera Camera = null!;

    // Belt outlines in map space (x, z), per line and segment, sampled once: the belt network never moves.
    // Drawn as two line lists (point pairs), working and broken, rebuilt when a segment's state changes.
    readonly List<Vector2[][]> _belts = [];
    readonly List<Vector2> _pairs = [];
    readonly List<bool> _brokenShown = [];
    Vector2[] _working = [], _broken = [], _covered = [];
    readonly List<Vector2> _packages = [], _dots = [];
    readonly Vector2[] _view = new Vector2[5];
    bool _dragging;

    public override void _Ready()
    {
        var bounds = Camera.Bounds;
        var size = new Vector2(Width, Width * bounds.Size.Y / bounds.Size.X);
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0, 0, 1, 1);
        (OffsetLeft, OffsetRight, OffsetTop, OffsetBottom) = (Margin, Margin + size.X, -Margin - size.Y, -Margin);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true; // belts run on beyond the map; the minimap shows only the map
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouse mouse) return;
        var world = ToWorld(mouse.Position);
        if (e.IsActionPressed("select")) { _dragging = true; LookAt(world); }
        else if (e.IsActionReleased("select")) _dragging = false;
        else if (e is InputEventMouseMotion && _dragging) LookAt(world);
        else if (e.IsActionPressed("act")) PlayerInput.MoveSelectionTo(world, Input.IsActionPressed("queue_order"));
        AcceptEvent();
    }

    void LookAt(SVector3 world) => Camera.FlyTo(new CameraView(new Vector2(world.X, world.Z), Camera.View.Distance), 0);

    public override void _Draw()
    {
        var state = Host.Sim.State;
        if (_belts.Count != state.Belts.Count) SampleBelts(state);
        DrawRect(new Rect2(Vector2.Zero, Size), Background);

        if (BeltStatesChanged(state)) BatchBelts(state);
        if (_covered.Length > 0) DrawMultiline(_covered, CoveredColor, 2);
        if (_working.Length > 0) DrawMultiline(_working, BeltColor, 2);
        if (_broken.Length > 0) DrawMultiline(_broken, BrokenColor, 2);
        _packages.Clear();
        foreach (var line in state.Belts)
            foreach (var p in line.Packages)
                if (Sight.SeesAt(p.Position)) AddDot(_packages, ToMap(p.Position), 1);
        if (_packages.Count > 0) DrawMultiline(_packages.ToArray(), PackageColor, 2);

        foreach (var rock in state.Obstacles) DrawObstacle(rock);
        foreach (var post in state.Gatherers)
            if (Sight.SeesStructure(post.Owner, post.Position, 1)) DrawPost(post.Position, post.Owner);
        foreach (var building in state.Buildings)
            if (Sight.SeesStructure(building.Owner, building.Position, building.Type.Size / 2)) DrawBuilding(building.Position, building.Type.Size, building.Owner, building.Built);
        // What it remembers and doesn't see now, as last seen.
        foreach (var ghost in Sight.Ghosts)
        {
            if (Sight.SeesArea(ghost.Position, ghost.IsPost ? 1 : ghost.Type!.Size / 2)) continue;
            if (ghost.IsPost) DrawPost(ghost.Position, ghost.Owner);
            else DrawBuilding(ghost.Position, ghost.Type!.Size, ghost.Owner, ghost.Built);
        }
        DrawUnits(state);
        if (!Sight.All && Host.Fog.Texture is { } fog) DrawTextureRect(fog, new Rect2(Vector2.Zero, Size), false, new Color(1, 1, 1, FogShade));
        DrawAlerts();
        DrawView();
        DrawRect(new Rect2(Vector2.Zero, Size), Border, filled: false, width: 1);
    }

    readonly Vector2[] _corners = new Vector2[4];
    const float FogShade = 0.6f;
    static readonly Color AlertColor = new(1, 0.25f, 0.2f);

    void DrawPost(SVector3 at, int owner) => DrawRect(new Rect2(ToMap(at) - new Vector2(2, 2), new Vector2(4, 4)), PlayerPalette.Color(owner));

    void DrawBuilding(SVector3 at, float size, int owner, bool built)
    {
        var half = (new Vector2(size, size) * PxPerMeter() / 2).Max(new Vector2(2, 2));
        DrawRect(new Rect2(ToMap(at) - half, half * 2), PlayerPalette.Color(owner), filled: built, width: built ? -1 : 1); // a filled rect takes no width
    }

    // A ring that keeps pulsing out from each alert while it lasts.
    void DrawAlerts()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (var (at, _) in Host.Alerts)
        {
            float t = (float)(now % 1.0);
            DrawArc(ToMap(at), 4 + 14 * t, 0, Mathf.Tau, 24, AlertColor with { A = 1 - t }, 2);
        }
    }

    void DrawObstacle(in Sim.Obstacle rock)
    {
        var (sin, cos) = System.MathF.SinCos(rock.Heading);
        var width = new SVector3(cos, 0, -sin) * rock.HalfWidth;
        var depth = new SVector3(sin, 0, cos) * rock.HalfDepth;
        (_corners[0], _corners[1]) = (ToMap(rock.Position - width - depth), ToMap(rock.Position + width - depth));
        (_corners[2], _corners[3]) = (ToMap(rock.Position + width + depth), ToMap(rock.Position - width + depth));
        DrawColoredPolygon(_corners, ObstacleColor);
    }

    // A dot per unit, one draw call per side (and one for the selection, drawn brighter on top).
    void DrawUnits(SimState state)
    {
        foreach (var player in state.Players)
        {
            _dots.Clear();
            foreach (var unit in state.Units)
                if (unit.Owner == player.Index && Sight.Sees(unit)) AddDot(_dots, ToMap(unit.Position), 1.5f);
            if (_dots.Count > 0) DrawMultiline(_dots.ToArray(), PlayerPalette.Color(player.Index), 3);
        }
        _dots.Clear();
        foreach (var unit in state.Units)
            if (System.Linq.Enumerable.Contains(PlayerInput.Selection, unit.Id)) AddDot(_dots, ToMap(unit.Position), 1.5f);
        if (_dots.Count > 0) DrawMultiline(_dots.ToArray(), Colors.White, 3);
    }

    // The ground the main camera sees: where its screen corners meet the ground (clamped to the map).
    void DrawView()
    {
        var screen = GetViewportRect().Size;
        Vector2[] corners = [Vector2.Zero, new(screen.X, 0), screen, new(0, screen.Y)];
        for (int i = 0; i < 4; i++)
        {
            if (Camera.GroundPoint(corners[i]) is not Vector3 ground) return; // looking above the horizon
            _view[i] = ToMap(new SVector3(ground.X, 0, ground.Z)).Clamp(Vector2.Zero, Size);
        }
        _view[4] = _view[0];
        DrawPolyline(_view, ViewColor, 1);
    }

    bool BeltStatesChanged(SimState state)
    {
        int i = 0;
        bool changed = false;
        for (int l = 0; l < state.Belts.Count; l++)
            for (int s = 0; s < state.Belts[l].Segments.Length; s++)
            {
                bool broken = Sight.SeenState(state, l, s) == SegmentState.Broken; // as the local player last saw it
                if (i == _brokenShown.Count)
                {
                    _brokenShown.Add(broken);
                    changed = true;
                }
                else if (_brokenShown[i] != broken)
                {
                    _brokenShown[i] = broken;
                    changed = true;
                }
                i++;
            }
        return changed;
    }

    // The three line lists (working, broken, covered), from the sampled outlines and each segment's state
    // as last seen.
    void BatchBelts(SimState state)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            _pairs.Clear();
            int i = 0;
            for (int l = 0; l < state.Belts.Count; l++)
                for (int s = 0; s < _belts[l].Length; s++, i++)
                {
                    bool covered = state.Belts[l].Segments[s].Covered;
                    int kind = covered ? 2 : _brokenShown[i] ? 1 : 0;
                    if (kind != pass) continue;
                    var points = _belts[l][s];
                    for (int k = 1; k < points.Length; k++) { _pairs.Add(points[k - 1]); _pairs.Add(points[k]); }
                }
            if (pass == 0) _working = _pairs.ToArray();
            else if (pass == 1) _broken = _pairs.ToArray();
            else _covered = _pairs.ToArray();
        }
    }

    void SampleBelts(SimState state)
    {
        _belts.Clear();
        _brokenShown.Clear();
        foreach (var line in state.Belts)
        {
            var segments = new Vector2[line.Segments.Length][];
            for (int s = 0; s < segments.Length; s++)
            {
                var curve = line.Segments[s].Curve;
                int steps = Mathf.Max(1, Mathf.CeilToInt(curve.Length / BeltSampleStep));
                segments[s] = new Vector2[steps + 1];
                for (int i = 0; i <= steps; i++) segments[s][i] = ToMap(curve.PositionAt(curve.Length * i / steps));
            }
            _belts.Add(segments);
        }
    }

    // A short horizontal stroke: a square dot once drawn `width` px thick.
    static void AddDot(List<Vector2> into, Vector2 at, float half)
    {
        into.Add(at - new Vector2(half, 0));
        into.Add(at + new Vector2(half, 0));
    }

    float PxPerMeter() => Size.X / Camera.Bounds.Size.X; // px per m

    Vector2 ToMap(SVector3 world)
    {
        var bounds = Camera.Bounds;
        return new Vector2(world.X - bounds.Position.X, world.Z - bounds.Position.Y) * PxPerMeter();
    }

    SVector3 ToWorld(Vector2 map)
    {
        var bounds = Camera.Bounds;
        var ground = bounds.Position + map / PxPerMeter();
        return new SVector3(ground.X, 0, ground.Y);
    }
}
