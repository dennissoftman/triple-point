using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// One scene instance per unit, a ground line through the current and queued orders of the selected
/// units, colored by what each order does, flickering tracers from bullet weapons, and shells in flight with a flash where they're fired and
/// where they land. Ballistic shells fly a high arc (drawn here; the sim flies them straight) and leave a
/// smoke trail that traces it as they go. Selected units with a minimum range (artillery) show their
/// reach: a ring at full range and one at the minimum. Under fog of war, enemy units, their shots and
/// shells show only where the local player sees them (Sight).
/// </summary>
public partial class UnitsView : Node3D
{
    const float PathHeight = 0.05f;  // just above the ground
    const float AimHeight = 0.5f;    // tracers end this far above what they hit
    const float WindowHeight = 1.6f; // m up a building's wall that those inside fire from
    const float FlashSeconds = 0.15f; // sim time a muzzle or impact flash lasts
    const float ArcRise = 0.28f;     // a ballistic shell's peak, as a share of the distance it flies
    const float PuffSeconds = 0.9f;  // sim time a smoke puff behind a ballistic shell lasts
    const float PuffEvery = 0.35f;   // m between puffs along the trail
    const int RingSegments = 72;
    const float WreckSeconds = 45f, SinkSeconds = 4f, SinkDepth = 1.5f; // sim time a vehicle's wreck stays, then sinks away
    const float SmokeSeconds = 8f, SmokeEvery = 0.2f;                   // a fresh wreck smoulders this long
    const float BoomSize = 5f;                                          // times the flash ball, when a vehicle blows up
    const float RingHeight = 0.07f;

    [Export] public PackedScene UnitScene = null!;
    [Export] public Material? FireMaterial;

    // Order path colors, by what the order does.
    static readonly Color MoveColor = new(0.55f, 1, 0.6f), AttackMoveColor = new(1, 0.6f, 0.15f),
        AttackColor = new(1, 0.25f, 0.2f), RepairColor = new(0.35f, 0.65f, 1),
        RangeColor = new(1, 0.75f, 0.3f), MinRangeColor = new(0.9f, 0.3f, 0.2f);
    static readonly StandardMaterial3D PathMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
    };

    readonly Dictionary<int, UnitView> _views = [];
    readonly Dictionary<int, Vector3> _positions = []; // interpolated, this frame
    readonly List<int> _gone = [];
    readonly List<(Vector3 From, Vector3 To, Color Color)> _legs = []; // order path legs, this frame
    readonly ImmediateMesh _lines = new();
    readonly UnitMaterials _materials = new();
    readonly Dictionary<int, (Vector3 At, float Radius)> _shellsAt = []; // where each shell will hit, for its impact flash
    readonly Dictionary<int, int> _lastShots = [];            // per unit: the shot tick last seen, to flash new ones
    readonly List<(Vector3 At, float Size, float Left)> _flashes = []; // size: times the flash mesh; sim seconds left
    readonly List<(Vector3 At, float Left)> _puffs = [];                // smoke behind ballistic shells; sim seconds left
    readonly Dictionary<int, Vector3> _lastPuff = [];                   // per ballistic shell: where it last left a puff
    readonly List<(UnitView View, float Left, float Smoke)> _wrecks = [];  // destroyed vehicles; sim seconds left, and to the next puff
    readonly StandardMaterial3D _burnt = new() { AlbedoColor = new Color(0.1f, 0.09f, 0.08f), Roughness = 1 };
    InstanceBatch _shellBatch = null!, _flashBatch = null!, _puffBatch = null!, _shadowBatch = null!;

    public override void _Ready()
    {
        AddChild(new MeshInstance3D { Mesh = _lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // Placeholder effects, drawn in code: a glowing slug, and a small bright ball for flashes.
        _shellBatch = new InstanceBatch(this, new BoxMesh { Size = new Vector3(0.14f, 0.14f, 0.7f), Material = Glow(new Color(1, 0.9f, 0.55f)) });
        _flashBatch = new InstanceBatch(this, new SphereMesh { Radius = 0.35f, Height = 0.7f, RadialSegments = 12, Rings = 6, Material = Glow(new Color(1, 0.6f, 0.2f)) });
        // A ballistic shell's shadow on the ground under it: seen from above, the arc reads by how far the
        // shell pulls away from its shadow.
        _shadowBatch = new InstanceBatch(this, new CylinderMesh
        {
            TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.02f, RadialSegments = 12,
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0, 0, 0, 0.45f),
            },
        });
        _puffBatch = new InstanceBatch(this, new SphereMesh
        {
            Radius = 0.22f, Height = 0.44f, RadialSegments = 8, Rings = 4,
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.85f, 0.83f, 0.78f, 0.55f),
            },
        });
    }

    static StandardMaterial3D Glow(Color color) => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color };

    /// <summary>A sim event, for effects. SimHost passes every one on.</summary>
    public void OnEvent(SimEvent e)
    {
        // A splash impact flashes as big as its blast (the flash ball is 0.35 m across the radius).
        if (e.Kind == SimEventKind.ShellHit && _shellsAt.Remove(e.Id, out var shell) && Sight.SeesAt(ToSim(shell.At)))
            _flashes.Add((shell.At, shell.Radius > 0 ? shell.Radius / 0.35f : 1, FlashSeconds));
    }

    /// <summary>`delta` is the sim time this frame covers, for member movement.</summary>
    public void Sync(Simulation sim, float alpha, float delta, IReadOnlyList<int> selection, PlayerInput.Placement? placing = null)
    {
        _lines.ClearSurfaces();
        _positions.Clear();
        foreach (var unit in sim.State.Units)
        {
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = UnitScene.Instantiate<UnitView>();
                AddChild(view);
                view.Setup(unit.Owner, _materials, unit.MaxMembers, unit.Movement, armed: unit.Damage > 0, heavyGunRange: unit.Ballistic ? unit.Range : 0,
                    cannon: unit.WeaponKind == WeaponKind.Shell);
                _views[unit.Id] = view;
            }
            var position = ToGodot(unit.PrevPosition).Lerp(ToGodot(unit.Position), alpha);
            float heading = Mathf.LerpAngle(unit.PrevHeading, unit.Heading, alpha);
            float turret = Mathf.LerpAngle(unit.PrevTurret, unit.Turret, alpha);
            _positions[unit.Id] = position;
            view.Sync(position, heading, turret, delta, unit.Members, unit.Health / unit.MaxHealth, unit.Firing, ToGodot(unit.FireAt),
                unit.CurrentAcceleration, unit.LateralAcceleration);
            view.Selected = selection.Contains(unit.Id);
            bool shown = unit.Inside < 0 && Sight.Sees(unit); // inside a building: the building shows it
            if (view.Visible != shown) view.Visible = shown;
            if (shown && unit.WeaponKind == WeaponKind.Shell && _lastShots.GetValueOrDefault(unit.Id, unit.LastShotTick) != unit.LastShotTick)
                _flashes.Add((view.Muzzles[0], 1, FlashSeconds));
            _lastShots[unit.Id] = unit.LastShotTick;
        }

        RemoveGone();
        UpdateWrecks(delta);
        DrawOrderPaths(sim, selection);
        DrawRanges(sim, selection, placing);
        DrawFire(sim.State);
        DrawShells(sim.State, alpha, delta);
    }

    void DrawShells(SimState state, float alpha, float delta)
    {
        _shellBatch.Begin(state.Projectiles.Count);
        _shadowBatch.Begin(state.Projectiles.Count);
        foreach (var p in state.Projectiles)
        {
            var (from, to) = (ToGodot(p.PrevPosition), ToGodot(p.Position));
            var at = from.Lerp(to, alpha);
            var direction = to - from is { } v && v.LengthSquared() > 1e-8f ? v.Normalized() : Vector3.Forward;
            _shellsAt[p.Id] = (ToGodot(p.Target), p.SplashRadius);
            if (!Sight.SeesAt(p.Position)) continue; // in the fog
            if (p.Ballistic)
            {
                _shadowBatch.Add(at with { Y = 0.06f }, Vector3.Forward);
                (at, direction) = Arc(p, at, direction);
            }
            _shellBatch.Add(at, direction); // its flash goes where the sim hits (_shellsAt), not where it was last drawn
            if (!p.Ballistic) continue;
            // Smoke every PuffEvery m of flight, so the trail draws itself along the arc as the shell goes.
            if (!_lastPuff.TryGetValue(p.Id, out var last)) last = at;
            for (float d = last.DistanceTo(at); d >= PuffEvery; d -= PuffEvery)
            {
                last = last.MoveToward(at, PuffEvery);
                _puffs.Add((last, PuffSeconds));
            }
            _lastPuff[p.Id] = last;
        }
        _shellBatch.End();
        _shadowBatch.End();
        if (_lastPuff.Count > state.Projectiles.Count) // shells that landed
        {
            _gone.Clear();
            foreach (int id in _lastPuff.Keys) if (!_shellsAt.ContainsKey(id) || !state.Projectiles.Exists(p => p.Id == id)) _gone.Add(id);
            foreach (int id in _gone) _lastPuff.Remove(id);
        }

        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var (at, left) = _puffs[i];
            if ((left -= delta) <= 0) _puffs.RemoveAt(i);
            else _puffs[i] = (at, left);
        }
        _puffBatch.Begin(_puffs.Count);
        foreach (var (at, left) in _puffs) _puffBatch.Add(at + new Vector3(0, (PuffSeconds - left) * 0.4f, 0), Vector3.Forward, 0.4f + 0.9f * left / PuffSeconds);
        _puffBatch.End();

        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var (at, size, left) = _flashes[i];
            if ((left -= delta) <= 0) _flashes.RemoveAt(i);
            else _flashes[i] = (at, size, left);
        }
        _flashBatch.Begin(_flashes.Count);
        foreach (var (at, size, _) in _flashes) _flashBatch.Add(at, Vector3.Forward, size);
        _flashBatch.End();
    }

    // Where a ballistic shell is drawn: lifted into a parabola over the straight path the sim flies it, from
    // where it was fired to where it lands, peaking at ArcRise of that distance; and its heading along it.
    static (Vector3 At, Vector3 Direction) Arc(in Projectile p, Vector3 at, Vector3 direction)
    {
        var origin = ToGodot(p.Origin);
        var target = ToGodot(p.Target);
        float total = new Vector2(target.X - origin.X, target.Z - origin.Z).Length();
        if (total < 0.01f) return (at, direction);
        float u = Mathf.Clamp(new Vector2(at.X - origin.X, at.Z - origin.Z).Length() / total, 0, 1);
        float rise = ArcRise * total;
        var ground = new Vector3(direction.X, 0, direction.Z).Normalized();
        var along = ground * total + Vector3.Up * (target.Y - origin.Y + rise * 4 * (1 - 2 * u)); // d(position)/du
        return (at + Vector3.Up * (rise * 4 * u * (1 - u)), along.Normalized());
    }

    // Rings on the ground at full and minimum range: around selected units that have a minimum range, and
    // selected defenses; and where a defense is being placed, the range its gun will have.
    void DrawRanges(Simulation sim, IReadOnlyList<int> selection, PlayerInput.Placement? placing)
    {
        bool drawing = false;
        foreach (var unit in sim.State.Units)
        {
            if (unit.Range <= 0 || (unit.MinRange <= 0 && unit.Movement != Movement.Static) || !selection.Contains(unit.Id)) continue;
            if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, PathMaterial); drawing = true; }
            var center = _positions[unit.Id] with { Y = RingHeight };
            Ring(center, unit.Range, RangeColor);
            if (unit.MinRange > 0) Ring(center, unit.MinRange, MinRangeColor);
        }
        if (placing is { Type.Defense: { } defense } p && defense.Gun.Range > 0)
        {
            if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, PathMaterial); drawing = true; }
            Ring(ToGodot(p.At) with { Y = RingHeight }, defense.Gun.Range, RangeColor);
        }
        if (drawing) _lines.SurfaceEnd();
    }

    void Ring(Vector3 center, float radius, Color color)
    {
        _lines.SurfaceSetColor(color);
        for (int i = 0; i < RingSegments; i++)
        {
            float a = Mathf.Tau * i / RingSegments, b = Mathf.Tau * (i + 1) / RingSegments;
            _lines.SurfaceAddVertex(center + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius);
            _lines.SurfaceAddVertex(center + new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b)) * radius);
        }
    }

    void RemoveGone()
    {
        if (_views.Count == _positions.Count) return;
        _gone.Clear();
        foreach (int id in _views.Keys)
            if (!_positions.ContainsKey(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            var view = _views[id];
            _views.Remove(id);
            _lastShots.Remove(id);
            if (!view.IsVehicle || !view.Visible) // died in the fog: nothing to see
            {
                view.QueueFree();
                continue;
            }
            // A vehicle blows up and leaves its wreck.
            view.Wreck(_burnt);
            _wrecks.Add((view, WreckSeconds, 0));
            _flashes.Add((view.GlobalPosition + new Vector3(0, 1, 0), BoomSize, FlashSeconds * 3));
            for (int k = 0; k < 10; k++)
                _puffs.Add((view.GlobalPosition + new Vector3(Mathf.Sin(k * 2.4f), 0.6f + k * 0.12f, Mathf.Cos(k * 2.4f)) * 0.9f, PuffSeconds));
        }
    }

    // Wrecks smoulder a while, stay, then sink into the ground and go. (Later they'll stay for salvage,
    // as sim state; for now they're only something to look at.)
    void UpdateWrecks(float delta)
    {
        for (int i = _wrecks.Count - 1; i >= 0; i--)
        {
            var (view, left, smoke) = _wrecks[i];
            left -= delta;
            if (left <= 0)
            {
                view.QueueFree();
                _wrecks.RemoveAt(i);
                continue;
            }
            if (WreckSeconds - left < SmokeSeconds && (smoke -= delta) <= 0)
            {
                smoke = SmokeEvery;
                _puffs.Add((view.GlobalPosition + new Vector3(0, 1.2f, 0), PuffSeconds));
            }
            if (left < SinkSeconds) view.Position = view.Position with { Y = -SinkDepth * (1 - left / SinkSeconds) };
            _wrecks[i] = (view, left, smoke);
        }
    }

    // Only the selected units' orders (the selection only ever holds your own). Current target first, then
    // each queued order's point; a queued segment order's point depends on where the leg before it ends.
    // Each leg takes its order's color.
    void DrawOrderPaths(Simulation sim, IReadOnlyList<int> selection)
    {
        _legs.Clear();
        foreach (var unit in sim.State.Units)
        {
            if (unit.Current.Kind == UnitOrder.None || !selection.Contains(unit.Id)) continue;
            var start = _positions[unit.Id];
            var target = unit.Current.Target;
            // The way it's actually going, round what's in the way, then on to the target if that's further.
            if (sim.PathOf(unit, out int next) is { } path)
                for (int i = next; i < path.Count; i++) AddLeg(ref start, ToGodot(path[i]), unit.Current.Kind);
            if (start.DistanceSquaredTo(ToGodot(target)) > 1) AddLeg(ref start, ToGodot(target), unit.Current.Kind);
            foreach (var order in unit.Pending)
            {
                target = sim.OrderPoint(order, target);
                AddLeg(ref start, ToGodot(target), order.Kind);
            }
        }
        if (_legs.Count == 0) return;
        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, PathMaterial);
        foreach (var (from, to, color) in _legs)
        {
            _lines.SurfaceSetColor(color);
            _lines.SurfaceAddVertex(from);
            _lines.SurfaceAddVertex(to);
        }
        _lines.SurfaceEnd();
    }

    void AddLeg(ref Vector3 start, Vector3 end, UnitOrder kind)
    {
        var color = kind switch
        {
            UnitOrder.AttackMove => AttackMoveColor,
            UnitOrder.Attack or UnitOrder.AttackSegment => AttackColor,
            UnitOrder.Repair or UnitOrder.Mend => RepairColor,
            _ => MoveColor,
        };
        _legs.Add((start with { Y = PathHeight }, end with { Y = PathHeight }, color));
        start = end;
    }

    // Bullet weapons: short tracer bursts, staggered per member so a squad doesn't fire like one gun.
    void DrawFire(SimState state)
    {
        bool drawing = false;
        foreach (var unit in state.Units)
        {
            if (unit.Firing && unit.WeaponKind == WeaponKind.Bullet && unit.Inside >= 0 && Sight.SeesAt(unit.Position))
            {
                // From inside a building: a burst from its windows, one per member, round its middle.
                if (!drawing) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines, FireMaterial); drawing = true; }
                var house = ToGodot(unit.Position);
                var target = ToGodot(unit.FireAt) + new Vector3(0, AimHeight, 0);
                var side = (target - house) with { Y = 0 };
                side = side.LengthSquared() > 0.01f ? side.Normalized() : Vector3.Forward;
                var across = new Vector3(-side.Z, 0, side.X);
                float depth = 0;
                foreach (var b in state.Buildings) if (b.Id == unit.Inside) depth = b.Type.Size / 2;
                for (int i = 0; i < unit.Members; i++)
                {
                    if ((state.Tick + i) % 4 >= 2) continue;
                    _lines.SurfaceAddVertex(house + side * depth + across * ((i - (unit.Members - 1) / 2f) * 0.6f) + new Vector3(0, WindowHeight, 0));
                    _lines.SurfaceAddVertex(target);
                }
                continue;
            }
            if (!unit.Firing || unit.WeaponKind != WeaponKind.Bullet || !_views[unit.Id].Visible) continue;
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

}
