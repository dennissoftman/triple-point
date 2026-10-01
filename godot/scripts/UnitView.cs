using System;
using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// The look of one unit. A squad is a capsule per member, walking loosely in a wedge behind the squad's
/// sim position (visual only: the sim has one position per squad). A vehicle is a hull on wheels or
/// tracks that follows the sim's heading and leans on its suspension as it speeds up, brakes and turns,
/// with a turret that follows the sim's turret; an unarmed one (a builder) carries a blade and a crane
/// instead, and a static one (a defense) sits on a squat base. Bodies are neutral and the side shows as an
/// accent seen from above (helmets, turret tops, the cab roof); each class has its own silhouette: rifles,
/// long launchers over the shoulder (rockets), a big pack (engineer); a long narrow car, a wide tank with a
/// long gun, artillery with its turret set back on a long hull. With a selection ring and a health bar.
/// UnitsView positions it.
/// </summary>
public partial class UnitView : Node3D
{
    const float TurnSharpness = 8f;    // squads turn toward their heading (1/s)
    const float FollowSharpness = 5f;  // how tightly members keep to their wedge slots (1/s)
    const float MemberRadius = 0.28f, MemberHeight = 1.3f, HelmetRadius = 0.24f;
    const float AccentThickness = 0.06f; // m: the side-colored plate on a turret's or cab's top
    const float SlotSpacing = 0.9f;    // m between members
    const float WheelRadius = 0.35f;
    // Suspension, for the look of weight only: the hull leans against acceleration (degrees per m/s²),
    // on a spring that overshoots a little and settles.
    const float SpringStiffness = 60f, SpringDamping = 9f; // 1/s², 1/s: a bit under critically damped
    const float MaxLean = 4f;                               // degrees

    [Export] public Node3D SelectionRing = null!;
    [Export] public int PlayerIndex; // for tools and debugging

    readonly List<MeshInstance3D> _members = [];  // squads: capsules
    readonly List<Vector3> _memberPositions = []; // squads: ground positions, eased toward their slots
    readonly List<Node3D> _wheels = [];           // wheeled vehicles: spin pivots
    readonly List<Vector3> _muzzles = [];
    readonly HealthBar _health = new();
    Node3D? _hull, _turret;                        // vehicles only
    Node3D? _hinge;                                // a heavy gun's barrel, raised by how far it's shooting
    float _gunRange, _elevation = RestElevation;   // heavy guns: m; degrees, as drawn
    float _turretHeight, _barrelLength;
    float _pitchPerAccel, _rollPerAccel;           // degrees per m/s²: tracks pitch more, wheels roll more
    Vector2 _lean, _leanSpeed;                     // (pitch, roll) in degrees, and how fast it's changing
    float _facing;                                 // yaw (radians) a squad faces
    float _wheelSpin, _healthFraction = 1;
    int _shownMembers = -1;
    Vector3 _lastPosition;
    bool _selected, _placed;

    /// <summary>Where shots come from this frame: the barrel tip, or each living member.</summary>
    public IReadOnlyList<Vector3> Muzzles => _muzzles;

    /// <summary>A vehicle (or a defense): it leaves a wreck when destroyed; squads just fall.</summary>
    public bool IsVehicle => _hull is not null;

    /// <summary>
    /// Turns a destroyed vehicle into its wreck: every part burnt to one dark material, no selection ring or
    /// health bar, the turret knocked askew and a raised barrel dropped. It never syncs again.
    /// </summary>
    public void Wreck(Material burnt)
    {
        SelectionRing.Visible = false;
        _health.Visible = false;
        foreach (var node in FindChildren("*", nameof(MeshInstance3D), true, false))
            if (node is MeshInstance3D mesh && node != SelectionRing) mesh.MaterialOverride = burnt;
        if (_turret is not null) _turret.Basis *= new Basis(Vector3.Up, 0.5f) * new Basis(Vector3.Right, 0.12f);
        if (_hinge is not null) _hinge.RotationDegrees = new Vector3(-6, 0, 0);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            SelectionRing.Visible = value;
            _health.Visible = value || _healthFraction < 1;
        }
    }

    // A heavy gun's barrel (artillery): at rest, and from its lowest to its highest, across its range.
    const float RestElevation = 12f, LowElevation = 18f, HighElevation = 45f, ElevationSpeed = 30f; // degrees; °/s

    /// <summary>`heavyGunRange` above 0: a long barrel that raises with the distance it shoots, up to that range.
    /// `cannon`: a defense that fires shells (the heavy turret) gets a big turret and a thick barrel; a
    /// squad that does (rockets) carries a launcher tube over each member's shoulder.</summary>
    public void Setup(int player, UnitMaterials materials, int members, Movement movement, bool armed, float heavyGunRange = 0, bool cannon = false)
    {
        PlayerIndex = player;
        var accent = materials.Accent(player);
        if (members > 1 || movement == Movement.Foot)
        {
            BuildSquad(members, materials.Infantry, accent);
            // Members face -Z (their facing, set in SyncSquad); +Z is the back, +X the right.
            if (!armed) // an engineer: a big tool pack on its back
                _members[0].AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.55f, 0.36f) }, MaterialOverride = materials.Dark, Position = new Vector3(0, 0.1f, 0.34f) });
            else if (cannon) // rockets: a long launcher over the right shoulder, along the facing
            {
                var tube = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.11f, Height = 1.4f };
                foreach (var member in _members)
                    member.AddChild(new MeshInstance3D { Mesh = tube, MaterialOverride = materials.Dark, Position = new Vector3(0.26f, 0.42f, 0.1f), RotationDegrees = new Vector3(90, 0, 0) });
            }
            else // rifles, held forward
            {
                var rifle = new BoxMesh { Size = new Vector3(0.06f, 0.06f, 0.7f) };
                foreach (var member in _members)
                    member.AddChild(new MeshInstance3D { Mesh = rifle, MaterialOverride = materials.Dark, Position = new Vector3(0.2f, 0.05f, -0.3f) });
            }
        }
        else BuildVehicle(movement, armed, materials.Vehicle, materials.Turret, accent, materials.Dark, heavyGunRange > 0, cannon && movement == Movement.Static);
        _gunRange = heavyGunRange;

        float ringRadius = members > 1 ? 2.1f : 1.9f;
        SelectionRing.Scale = new Vector3(ringRadius / 0.9f, 0.2f, ringRadius / 0.9f); // the ring mesh is 0.9 m
        _health.Position = new Vector3(0, 2.1f, 0);
        _health.Visible = false;
        AddChild(_health);
    }

    void BuildSquad(int members, Material body, Material accent)
    {
        var mesh = new CapsuleMesh { Radius = MemberRadius, Height = MemberHeight };
        var helmet = new SphereMesh { Radius = HelmetRadius, Height = HelmetRadius * 1.4f };
        for (int i = 0; i < members; i++)
        {
            var member = new MeshInstance3D { Mesh = mesh, MaterialOverride = body };
            member.AddChild(new MeshInstance3D { Mesh = helmet, MaterialOverride = accent, Position = new Vector3(0, MemberHeight / 2 - HelmetRadius * 0.5f, 0) });
            AddChild(member);
            _members.Add(member);
            _memberPositions.Add(Vector3.Zero);
        }
    }

    // Forward is -Z throughout, as Basis.LookingAt expects.
    void BuildVehicle(Movement movement, bool armed, Material body, Material turretBody, Material accent, Material dark, bool heavyGun, bool cannon)
    {
        bool tracked = movement == Movement.Tracked, fixedBase = movement == Movement.Static;
        (_pitchPerAccel, _rollPerAccel) = tracked ? (0.9f, 0.3f) : (0.7f, 0.45f);
        // Hulls by class: a squat base (defense), a long one (artillery), a wide one (tank), a builder's,
        // a long narrow car.
        var hull = fixedBase ? new Vector3(1.8f, 0.8f, 1.8f)
            : heavyGun ? new Vector3(1.6f, 0.6f, 3.2f)
            : tracked && armed ? new Vector3(2.0f, 0.65f, 2.7f)
            : tracked ? new Vector3(1.7f, 0.7f, 2.4f)
            : new Vector3(1.2f, 0.45f, 2.4f);
        float hullBottom = fixedBase ? 0 : tracked ? 0.35f : WheelRadius + 0.1f;

        _hull = new Node3D();
        AddChild(_hull);
        AddBox(_hull, hull, body, new Vector3(0, hullBottom + hull.Y / 2, 0));
        float top = hullBottom + hull.Y;
        if (!armed)
        {
            // A builder: a dozer blade in front, a cab with a side-colored roof, and a crane arm reaching back.
            AddBox(_hull, new Vector3(hull.X + 0.5f, 0.55f, 0.18f), dark, new Vector3(0, 0.3f, -(hull.Z / 2 + 0.35f)));
            AddBox(_hull, new Vector3(1.0f, 0.55f, 0.9f), turretBody, new Vector3(0, top + 0.275f, -0.5f));
            AddBox(_hull, new Vector3(0.9f, AccentThickness, 0.8f), accent, new Vector3(0, top + 0.55f + AccentThickness / 2, -0.5f));
            AddBox(_hull, new Vector3(0.18f, 1.6f, 0.18f), dark, new Vector3(0.4f, top + 0.8f, 0.7f));
            var boom = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 0.14f, 1.8f) }, MaterialOverride = dark, Position = new Vector3(0.4f, top + 1.45f, 1.25f), RotationDegrees = new Vector3(-25, 0, 0) };
            _hull.AddChild(boom);
        }
        for (int side = -1; side <= 1 && !fixedBase; side += 2)
        {
            if (tracked)
            {
                AddBox(_hull, new Vector3(0.45f, 0.65f, hull.Z + 0.2f), dark, new Vector3(side * (hull.X / 2 + 0.2f), 0.33f, 0));
                continue;
            }
            for (int end = -1; end <= 1; end += 2)
            {
                var pivot = new Node3D { Position = new Vector3(side * (hull.X / 2 + 0.1f), WheelRadius, end * 0.75f) };
                _hull.AddChild(pivot);
                pivot.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = WheelRadius, BottomRadius = WheelRadius, Height = 0.28f },
                    MaterialOverride = dark,
                    RotationDegrees = new Vector3(0, 0, 90), // axle along X
                });
                _wheels.Add(pivot);
            }
        }

        // The turret isn't parented to the hull: it aims on its own. Artillery's sits toward the back.
        _turretHeight = hullBottom + hull.Y;
        if (!armed) return;
        _turret = new Node3D { Position = new Vector3(0, _turretHeight, 0) };
        AddChild(_turret);
        var turret = heavyGun ? new Vector3(1.1f, 0.55f, 1.2f) : cannon ? new Vector3(1.2f, 0.6f, 1.4f)
            : tracked ? new Vector3(1.3f, 0.45f, 1.4f) : new Vector3(0.55f, 0.3f, 0.6f);
        float back = heavyGun ? 0.8f : 0; // m behind the turret ring the box sits
        AddBox(_turret, turret, turretBody, new Vector3(0, turret.Y / 2, back));
        AddBox(_turret, new Vector3(turret.X * 0.8f, AccentThickness, turret.Z * 0.8f), accent, new Vector3(0, turret.Y + AccentThickness / 2, back));
        _barrelLength = heavyGun ? 2.6f : cannon ? 2.0f : tracked ? 2.0f : 0.9f;
        if (heavyGun)
        {
            // A long barrel raised toward the sky, hinged at the front of the turret.
            _hinge = new Node3D { Position = new Vector3(0, turret.Y * 0.7f, back - turret.Z / 2), RotationDegrees = new Vector3(RestElevation, 0, 0) };
            _turret.AddChild(_hinge);
            AddBox(_hinge, new Vector3(0.2f, 0.2f, _barrelLength), dark, new Vector3(0, 0, -_barrelLength / 2));
        }
        else
        {
            float bore = cannon ? 0.26f : tracked ? 0.2f : 0.1f;
            AddBox(_turret, new Vector3(bore, bore, _barrelLength), dark, new Vector3(0, turret.Y / 2, -(turret.Z + _barrelLength) / 2));
        }
    }

    static void AddBox(Node3D parent, Vector3 size, Material material, Vector3 at) =>
        parent.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = material, Position = at });

    /// <summary>
    /// Moves the unit to its interpolated sim position, heading and turret yaw (radians). `delta` is sim
    /// time for this frame, so squad members keep pace at any game speed.
    /// </summary>
    public void Sync(Vector3 position, float heading, float turret, float delta, int members, float health, bool firing, Vector3 fireAt,
        float acceleration = 0, float lateral = 0)
    {
        float moved = _placed ? (position - _lastPosition).Dot(Direction(heading)) : 0; // negative when backing up
        GlobalPosition = position;
        _muzzles.Clear();
        if (_hull is not null) SyncVehicle(position, heading, turret, moved, Lean(acceleration, lateral, delta));
        if (_hinge is not null) Elevate(position, firing, fireAt, delta);
        else SyncSquad(position, delta, members, firing, fireAt);
        _lastPosition = position;
        _placed = true;

        if (health != _healthFraction)
        {
            _healthFraction = health;
            _health.Set(health, HealthBar.HealthColor(health));
            _health.Visible = _selected || health < 1;
        }
    }

    // A heavy gun raises its barrel with the distance to what it's shooting at, low for close targets and
    // up to HighElevation at full range, and lowers it to rest when it isn't shooting; it eases between.
    void Elevate(Vector3 position, bool firing, Vector3 fireAt, float delta)
    {
        float want = RestElevation;
        if (firing)
        {
            float share = Mathf.Clamp(new Vector2(fireAt.X - position.X, fireAt.Z - position.Z).Length() / _gunRange, 0, 1);
            want = Mathf.Lerp(LowElevation, HighElevation, share);
        }
        float next = Mathf.MoveToward(_elevation, want, ElevationSpeed * delta);
        if (next == _elevation) return;
        _elevation = next;
        _hinge!.RotationDegrees = new Vector3(_elevation, 0, 0);
    }

    // The sim aims the turret (it tracks targets before they're in range, and fires once on them), so the
    // view just follows it.
    void SyncVehicle(Vector3 position, float heading, float turret, float moved, Basis lean)
    {
        var yaw = Basis.LookingAt(Direction(heading), Vector3.Up);
        _hull!.Basis = yaw * lean;
        _wheelSpin -= moved / WheelRadius; // rolls forward, or backward when reversing
        foreach (var wheel in _wheels) wheel.Rotation = new Vector3(_wheelSpin, 0, 0);

        if (_turret is null) return;
        // The turret sits on the hull, so it leans with it (the lean taken into world space).
        var tilt = yaw * lean * yaw.Inverse();
        var aim = Direction(turret);
        _turret.Basis = tilt * Basis.LookingAt(aim, Vector3.Up);
        _turret.Position = tilt * new Vector3(0, _turretHeight, 0);
        _muzzles.Add(position + new Vector3(0, _turretHeight + 0.2f, 0) + aim * (_barrelLength + 0.5f));
    }

    void SyncSquad(Vector3 position, float delta, int alive, bool firing, Vector3 fireAt)
    {
        // Face where it's going, or what it's shooting at.
        if (Yaw(firing ? fireAt - position : position - _lastPosition) is float want)
            _facing = _placed ? Mathf.LerpAngle(_facing, want, 1 - MathF.Exp(-TurnSharpness * delta)) : want;
        var forward = Direction(_facing);
        var right = forward.Cross(Vector3.Up);
        float follow = _placed ? 1 - MathF.Exp(-FollowSharpness * delta) : 1; // snap into place on the first frame

        if (alive != _shownMembers) // the rearmost members fall first
        {
            _shownMembers = alive;
            for (int i = 0; i < _members.Count; i++) _members[i].Visible = i < alive;
        }
        var facing = Basis.LookingAt(forward, Vector3.Up);
        for (int i = 0; i < alive && i < _members.Count; i++)
        {
            var slot = WedgeSlot(i);
            _memberPositions[i] = _memberPositions[i].Lerp(position + right * slot.X + forward * slot.Y, follow);
            _members[i].GlobalTransform = new Transform3D(facing, _memberPositions[i] + new Vector3(0, MemberHeight / 2, 0));
            _muzzles.Add(_memberPositions[i] + new Vector3(0, MemberHeight * 0.7f, 0));
        }
    }

    // Speeding up squats the rear (nose up), braking dips the nose, and a turn rolls the hull to the outside.
    // `acceleration` is along the heading, `lateral` toward its left, both m/s² from the sim.
    Basis Lean(float acceleration, float lateral, float delta)
    {
        var target = new Vector2(
            Math.Clamp(acceleration * _pitchPerAccel, -MaxLean, MaxLean),
            Math.Clamp(-lateral * _rollPerAccel, -MaxLean, MaxLean));
        if (!_placed) (_lean, _leanSpeed) = (target, Vector2.Zero);
        // Semi-implicit Euler, in small steps so a long frame stays stable.
        for (float left = delta; left > 0; left -= 1 / 60f)
        {
            float dt = Math.Min(left, 1 / 60f);
            _leanSpeed += (SpringStiffness * (target - _lean) - SpringDamping * _leanSpeed) * dt;
            _lean += _leanSpeed * dt;
        }
        // Local axes after LookingAt: +X right, -Z forward. Positive X rotation lifts the nose; positive Z
        // rotation lifts the right side.
        return Basis.FromEuler(new Vector3(Mathf.DegToRad(_lean.X), 0, Mathf.DegToRad(_lean.Y)));
    }

    // The sim's heading convention: yaw h faces (sin h, 0, cos h).
    static Vector3 Direction(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));

    static float? Yaw(Vector3 v) => v.X * v.X + v.Z * v.Z > 1e-6f ? MathF.Atan2(v.X, v.Z) : null;

    // A wedge: member 0 at the point, then pairs further back and wider, with a little fixed jitter so
    // the squad doesn't look like a grid. X is right, Y is forward, in meters.
    static Vector2 WedgeSlot(int i)
    {
        int row = (i + 1) / 2;
        float side = i == 0 ? 0 : i % 2 == 1 ? -1 : 1;
        float jitterX = (((i * 37) % 11) / 10f - 0.5f) * 0.3f;
        float jitterY = (((i * 53) % 7) / 6f - 0.5f) * 0.3f;
        return new Vector2(side * row * SlotSpacing + jitterX, (0.8f - row * 0.8f) * SlotSpacing + jitterY);
    }
}
