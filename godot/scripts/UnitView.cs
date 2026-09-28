using System;
using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// The look of one unit. A squad is a capsule per member, walking loosely in a wedge behind the squad's
/// sim position (visual only: the sim has one position per squad). A vehicle is a hull on wheels or
/// tracks that follows the sim's heading and leans on its suspension as it speeds up, brakes and turns,
/// with a turret that follows the sim's turret; an unarmed one (a builder) carries a blade instead, and a
/// static one (a defense) sits on a squat base. Tinted by
/// owner, with a selection ring and a health bar. UnitsView positions it.
/// </summary>
public partial class UnitView : Node3D
{
    const float TurnSharpness = 8f;    // squads turn toward their heading (1/s)
    const float FollowSharpness = 5f;  // how tightly members keep to their wedge slots (1/s)
    const float MemberRadius = 0.28f, MemberHeight = 1.3f;
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

    public void Setup(int player, UnitMaterials materials, int members, Movement movement, bool armed)
    {
        PlayerIndex = player;
        var (body, turret) = materials.For(player);
        if (members > 1) BuildSquad(members, body);
        else BuildVehicle(movement, armed, body, turret, materials.Dark);

        float ringRadius = members > 1 ? 2.1f : 1.9f;
        SelectionRing.Scale = new Vector3(ringRadius / 0.9f, 0.2f, ringRadius / 0.9f); // the ring mesh is 0.9 m
        _health.Position = new Vector3(0, 2.1f, 0);
        _health.Visible = false;
        AddChild(_health);
    }

    void BuildSquad(int members, Material body)
    {
        var mesh = new CapsuleMesh { Radius = MemberRadius, Height = MemberHeight };
        for (int i = 0; i < members; i++)
        {
            var member = new MeshInstance3D { Mesh = mesh, MaterialOverride = body };
            AddChild(member);
            _members.Add(member);
            _memberPositions.Add(Vector3.Zero);
        }
    }

    // Forward is -Z throughout, as Basis.LookingAt expects.
    void BuildVehicle(Movement movement, bool armed, Material body, Material turretBody, Material dark)
    {
        bool tracked = movement == Movement.Tracked, fixedBase = movement == Movement.Static;
        (_pitchPerAccel, _rollPerAccel) = tracked ? (0.9f, 0.3f) : (0.7f, 0.45f);
        var hull = fixedBase ? new Vector3(1.8f, 0.8f, 1.8f) : tracked ? new Vector3(1.7f, 0.7f, 2.5f) : new Vector3(1.4f, 0.5f, 2.2f);
        float hullBottom = fixedBase ? 0 : tracked ? 0.35f : WheelRadius + 0.1f;

        _hull = new Node3D();
        AddChild(_hull);
        AddBox(_hull, hull, body, new Vector3(0, hullBottom + hull.Y / 2, 0));
        if (!armed) AddBox(_hull, new Vector3(hull.X + 0.5f, 0.55f, 0.18f), dark, new Vector3(0, 0.3f, -(hull.Z / 2 + 0.35f))); // a dozer blade
        for (int side = -1; side <= 1 && !fixedBase; side += 2)
        {
            if (tracked)
            {
                AddBox(_hull, new Vector3(0.45f, 0.65f, 2.7f), dark, new Vector3(side * (hull.X / 2 + 0.2f), 0.33f, 0));
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

        // The turret isn't parented to the hull: it aims on its own.
        _turretHeight = hullBottom + hull.Y;
        if (!armed) return;
        _turret = new Node3D { Position = new Vector3(0, _turretHeight, 0) };
        AddChild(_turret);
        var turret = tracked ? new Vector3(1.0f, 0.4f, 1.1f) : new Vector3(0.65f, 0.3f, 0.75f);
        AddBox(_turret, turret, turretBody, new Vector3(0, turret.Y / 2, 0));
        _barrelLength = tracked ? 1.4f : 0.8f;
        AddBox(_turret, new Vector3(0.14f, 0.14f, _barrelLength), dark, new Vector3(0, turret.Y / 2, -(turret.Z + _barrelLength) / 2));
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
        for (int i = 0; i < alive && i < _members.Count; i++)
        {
            var slot = WedgeSlot(i);
            _memberPositions[i] = _memberPositions[i].Lerp(position + right * slot.X + forward * slot.Y, follow);
            _members[i].GlobalPosition = _memberPositions[i] + new Vector3(0, MemberHeight / 2, 0);
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
