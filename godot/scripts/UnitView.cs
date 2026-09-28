using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The look of one unit: a box with a turret for a vehicle, or a capsule per member for a squad. Members
/// walk loosely in a wedge behind the squad's sim position (visual only: the sim has one position per
/// squad). Tinted by owner, with a selection ring and a health bar. UnitsView positions it.
/// </summary>
public partial class UnitView : Node3D
{
    const float TurnSharpness = 8f;    // how fast the unit turns toward its heading (1/s)
    const float FollowSharpness = 5f;  // how tightly members keep to their wedge slots (1/s)
    const float MemberRadius = 0.28f, MemberHeight = 1.3f;
    const float SlotSpacing = 0.9f;    // m between members
    static readonly Vector3 HullSize = new(1.6f, 0.9f, 2.2f);

    [Export] public Node3D SelectionRing = null!;
    [Export] public int PlayerIndex; // for tools and debugging

    readonly List<MeshInstance3D> _members = [];  // capsules; empty for a vehicle
    readonly List<Vector3> _memberPositions = []; // ground positions, eased toward their slots
    readonly List<Vector3> _muzzles = [];
    readonly HealthBar _health = new();
    Node3D _hull = null!; // vehicles: turns with the heading
    Vector3 _heading = Vector3.Forward;
    Vector3 _lastPosition;
    float _healthFraction = 1;
    bool _selected, _placed;

    /// <summary>Where shots come from this frame: the turret, or each living member.</summary>
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

    public void Setup(int player, Color color, int members)
    {
        PlayerIndex = player;
        var body = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.7f };
        if (members <= 1)
        {
            _hull = new Node3D();
            AddChild(_hull);
            _hull.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = HullSize },
                MaterialOverride = body,
                Position = new Vector3(0, HullSize.Y / 2, 0),
            });
            _hull.AddChild(new MeshInstance3D // turret and barrel, pointing forward (-Z)
            {
                Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.35f, 1.3f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color.Darkened(0.35f) },
                Position = new Vector3(0, HullSize.Y + 0.17f, -0.35f),
            });
        }
        else
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

        float ringRadius = members <= 1 ? 1.6f : 2.1f;
        SelectionRing.Scale = new Vector3(ringRadius / 0.9f, 0.2f, ringRadius / 0.9f); // the ring mesh is 0.9 m
        _health.Position = new Vector3(0, 2f, 0);
        _health.Visible = false;
        AddChild(_health);
    }

    /// <summary>
    /// Moves the unit to its interpolated sim position. `delta` is sim time for this frame, so members
    /// keep pace at any game speed.
    /// </summary>
    public void Sync(Vector3 position, float delta, int members, float health, bool firing, Vector3 fireAt)
    {
        // Face where it's going, or what it's shooting at.
        var toward = firing ? fireAt - position : position - _lastPosition;
        toward.Y = 0;
        if (toward.LengthSquared() > 1e-6f)
            _heading = _heading.Lerp(toward.Normalized(), 1 - MathF.Exp(-TurnSharpness * delta)).Normalized();
        _lastPosition = position;
        GlobalPosition = position;

        _muzzles.Clear();
        if (_members.Count == 0)
        {
            _hull.Basis = Basis.LookingAt(_heading, Vector3.Up);
            _muzzles.Add(position + new Vector3(0, HullSize.Y + 0.2f, 0) + _heading * 1.0f);
        }
        else SyncMembers(position, delta, members);

        if (health != _healthFraction)
        {
            _healthFraction = health;
            _health.Set(health, HealthBar.HealthColor(health));
            _health.Visible = _selected || health < 1;
        }
    }

    void SyncMembers(Vector3 position, float delta, int alive)
    {
        var right = _heading.Cross(Vector3.Up);
        float follow = _placed ? 1 - MathF.Exp(-FollowSharpness * delta) : 1; // snap into place on the first frame
        _placed = true;
        for (int i = 0; i < _members.Count; i++)
        {
            // The rearmost members fall first.
            bool living = i < alive;
            if (_members[i].Visible != living) _members[i].Visible = living;
            if (!living) continue;

            var slot = WedgeSlot(i);
            var target = position + right * slot.X + _heading * slot.Y;
            _memberPositions[i] = _memberPositions[i].Lerp(target, follow);
            _members[i].GlobalPosition = _memberPositions[i] + new Vector3(0, MemberHeight / 2, 0);
            _muzzles.Add(_memberPositions[i] + new Vector3(0, MemberHeight * 0.7f, 0));
        }
    }

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
