using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws the belts, packages, spilled pickups, and gatherer posts. A belt reads as a machine made of
/// pieces: a dark surface between raised rails, on a low base, with a crossbar at every joint between
/// segments, so each segment is visibly a thing that can be shot apart. Damage tints a segment and shows
/// its health bar; a broken one buckles into two halves torn up at the break with debris in the gap,
/// settling back flat as repair brings its health back. The segment under the cursor, with units
/// selected, lights up in the color of what a right-click would do (PlayerInput sets Hover). Covered
/// stretches are a closed housing over the belt, dark where the open belt comes out. While a post is
/// being placed, green strips beside the belts mark where one can go (ShowPostSpots). A spilled package
/// jumps off the belt tumbling and lands; one that breaks in the fall bursts into shards. A finite source
/// shows what it has left as a gauge on the housing where its belt comes into play.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between belt cross-sections
    const float FlashTicks = 6;     // a gatherer glows this long after a grab
    static readonly Vector3 PostSize = new(1.6f, 1.2f, 1.6f);

    // The belt's cross-section, m: rails either side of the surface, standing above it, and a base down
    // to the ground. Joints are crossbars a little wider and taller than the belt.
    const float RailWidth = 0.14f, RailHeight = 0.16f;
    const float JointLength = 0.3f, JointOverhang = 0.15f, JointRise = 0.06f;
    const float BreakGap = 0.5f;     // m between a broken segment's halves
    const float BuckleDegrees = 28f; // how far a broken segment's halves tear up at the break, at 0 health
    const float TwistDegrees = 10f;  // and roll sideways, opposite ways, so it reads as wreckage
    const float HealthBarHeight = 1.4f;
    const float HousingHeight = 0.8f, HousingOverhang = 0.12f; // m above the surface, and beyond the rails
    const float SpotWidth = 0.5f, SpotHeight = 0.04f, SpotStep = 0.5f; // the free-spot strips beside the belt
    const float FallHop = 0.9f, FallTurns = 1.25f; // a spilled package's jump off the belt, on average (its time is the sim's)
    const float CollectSeconds = 0.35f, PopSeconds = 1.1f, PopRise = 2.2f; // a collected pickup flies to its unit; the +1 floats up
    const int ShardCount = 5;
    const float ShardSize = 0.26f, ShardSpeed = 2.6f, ShardRise = 2.4f, ShardSeconds = 0.9f, Gravity = 9.8f;
    const float GaugeBack = 2f, GaugeHeight = 2.2f; // m: back from where the open belt starts, and above the ground

    public enum HoverKind { None, Look, Attack, Repair }

    [Export] public StandardMaterial3D BeltMaterial = null!; // its albedo is the belt surface's color
    [Export] public Material? PackageMaterial;
    [Export] public Color RailColor = new(0.55f, 0.57f, 0.6f);
    [Export] public Color BaseColor = new(0.22f, 0.22f, 0.24f);
    [Export] public Color JointColor = new(0.36f, 0.37f, 0.4f);
    [Export] public Color DebrisColor = new(0.3f, 0.3f, 0.32f);
    [Export] public Color HousingColor = new(0.34f, 0.36f, 0.35f);
    [Export] public Color HousingRoofColor = new(0.42f, 0.44f, 0.42f);
    [Export] public Color SpotColor = new(0.4f, 1f, 0.5f, 0.55f);
    [Export] public Color SupplyColor = new(0.95f, 0.65f, 0.2f); // the source gauge; the packages' color
    [Export] public Color DamagedTint = new(1f, 0.55f, 0.25f); // multiplies a working segment's colors as health drops
    [Export] public Color BrokenTint = new(1f, 0.4f, 0.32f);   // and a broken one's, fading as repair restores health
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;
    [Export] public StandardMaterial3D GathererMaterial = null!;

    /// <summary>The segment under the cursor and what a right-click on it would do; set by PlayerInput.</summary>
    public (int Line, int Segment, HoverKind Kind) Hover = (-1, -1, HoverKind.None);

    // What each view last showed is kept here in C#, so a frame only calls into the engine for what changed.
    sealed record PostView(Node3D Root, StandardMaterial3D Material, HealthBar Health)
    {
        public bool Glowing;
    }

    // One per segment, indexed [line][segment]. Its own material, so each can show its own state. (Dev-grade:
    // a draw call per segment. See the rendering debt in docs/architecture.md.) The wreck and the health
    // bar are built the first time they're needed.
    sealed class SegmentView
    {
        public MeshInstance3D Whole = null!;
        public StandardMaterial3D Material = null!;
        public Node3D? Wreck;
        public Node3D HalfA = null!, HalfB = null!;
        public HealthBar? Health;
        public Color Tint = Colors.White;
        public float Buckle = -1; // 0..1 as drawn; -1: whole
        public HoverKind Hover;
        public float HealthShown = 1;
    }

    readonly List<SegmentView[]> _segments = [];
    readonly List<(HealthBar Bar, Label3D Label, int Shown)?> _gauges = []; // per line; null for an unlimited source
    readonly Dictionary<int, (Vector3 At, float Yaw)> _smashing = []; // smashed pickups in the air, by id: where they'll land
    readonly List<int> _landed = [];
    readonly List<(Vector3 From, Vector3 Velocity, Vector3 Axis, float Born)> _shards = [];
    readonly Dictionary<int, Vector3> _pickupAt = []; // where each pickup was last drawn, for its collection
    readonly List<(Vector3 From, int Unit, float Yaw, float Born)> _collecting = [];
    readonly List<(Label3D Label, Vector3 From, float Born)> _pops = [];
    readonly Stack<Label3D> _spareLabels = [];
    float _seconds; // sim time as last drawn
    readonly Dictionary<int, PostView> _posts = []; // by gatherer id; destroyed posts go away
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    InstanceBatch _packages = null!, _pickups = null!, _shardBatch = null!, _collectBatch = null!;
    StandardMaterial3D _debrisMaterial = null!, _railDebrisMaterial = null!;
    MeshInstance3D _spots = null!;

    public void Build(SimState state)
    {
        _debrisMaterial = new StandardMaterial3D { AlbedoColor = DebrisColor, Roughness = 0.9f };
        _railDebrisMaterial = new StandardMaterial3D { AlbedoColor = RailColor * BrokenTint, Roughness = 0.6f };
        foreach (var line in state.Belts)
        {
            var views = new SegmentView[line.Segments.Length];
            for (int s = 0; s < line.Segments.Length; s++)
            {
                var material = (StandardMaterial3D)BeltMaterial.Duplicate();
                material.VertexColorUseAsAlbedo = true;
                material.VertexColorIsSrgb = true;   // the colors are authored like albedo colors
                material.AlbedoColor = Colors.White; // the tint; the colors themselves are in the vertices
                material.EmissionEnabled = true;     // always on, so lighting up on hover doesn't swap shaders
                material.EmissionEnergyMultiplier = 0;
                var curve = line.Segments[s].Curve;
                bool last = s == line.Segments.Length - 1;
                bool covered = line.Segments[s].Covered;
                var whole = new MeshInstance3D { Mesh = BuildSection(curve, 0, curve.Length, Vector3.Zero, joint: true, endJoint: last, covered), MaterialOverride = material };
                AddChild(whole);
                views[s] = new SegmentView { Whole = whole, Material = material };
            }
            _segments.Add(views);
            _gauges.Add(line.Finite ? BuildGauge(line) : null);
        }

        var box = new BoxMesh { Size = Vector3.One * PackageSize, Material = PackageMaterial };
        _packages = new InstanceBatch(this, box);
        _pickups = new InstanceBatch(this, box);
        _shardBatch = new InstanceBatch(this, new BoxMesh { Size = Vector3.One * ShardSize, Material = PackageMaterial });
        _collectBatch = new InstanceBatch(this, box);
        AddChild(_spots = new MeshInstance3D
        {
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = SpotColor,
            },
        });
    }

    /// <summary>
    /// Marks where a post can go: a strip on each side of the belt, at a post's distance from it, along
    /// every free stretch; null hides them. Rebuilt on each call, so call it only when they change.
    /// </summary>
    public void ShowPostSpots(SimState state, IReadOnlyList<(int Line, float From, float To)>? spots)
    {
        _spots.Visible = spots is { Count: > 0 };
        if (!_spots.Visible) return;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (l, from, to) in spots!)
        {
            var line = state.Belts[l];
            int steps = Math.Max(1, (int)MathF.Ceiling((to - from) / SpotStep));
            for (int sign = -1; sign <= 1; sign += 2)
                for (int i = 0; i < steps; i++)
                {
                    Vector3 Edge(int k, float o)
                    {
                        float d = from + (to - from) * k / steps;
                        var side = ToGodot(line.DirectionAt(d)).Cross(Vector3.Up).Normalized();
                        return (ToGodot(line.PositionAt(d)) with { Y = SpotHeight }) + side * (sign * (Simulation.PostOffset + o));
                    }
                    Quad(st, Edge(i, -SpotWidth / 2), Edge(i, SpotWidth / 2), Edge(i + 1, SpotWidth / 2), Edge(i + 1, -SpotWidth / 2), Vector3.Up, Colors.White);
                }
        }
        _spots.Mesh = st.Commit();
    }

    public void Sync(SimState state, float alpha)
    {
        var lift = new Vector3(0, PackageSize / 2, 0); // sit on the surface, not in it

        int total = 0;
        foreach (var line in state.Belts) total += line.Packages.Count;
        _packages.Begin(total);
        for (int l = 0; l < state.Belts.Count; l++)
        {
            var line = state.Belts[l];
            SyncSegments(l, line, _segments[l]);
            foreach (var p in line.Packages)
                _packages.Add(ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) + lift, ToGodot(p.Direction));
        }
        _packages.End();

        SyncPosts(state);
        SyncGauges(state);

        // A spilled package jumps off the belt, tumbling, and lands at rest where the sim put it. Each
        // one's hop, tumble axis and spin are its own, from its id.
        float now = state.Tick - 1 + alpha; // the tick being drawn, as positions interpolate
        _seconds = now / Simulation.TicksPerSecond;
        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            float yaw = Random01(p.Id, 0) * MathF.Tau;
            var rest = new Basis(Vector3.Up, yaw);
            float t = Math.Clamp((now - p.SpilledAtTick) / (p.LandsAtTick - p.SpilledAtTick), 0, 1);
            if (p.Smashed) _smashing[p.Id] = (ToGodot(p.Position) + lift, yaw);
            _pickupAt[p.Id] = ToGodot(p.Position) + lift;
            if (t >= 1)
            {
                _pickups.Add(new Transform3D(rest, ToGodot(p.Position) + lift));
                continue;
            }
            float hop = FallHop * (0.7f + 0.6f * Random01(p.Id, 1));
            float turns = FallTurns * (0.5f + Random01(p.Id, 2)) * (Random01(p.Id, 3) < 0.5f ? -1 : 1);
            float tilt = Random01(p.Id, 4) * MathF.Tau;
            var axis = new Vector3(MathF.Cos(tilt), 0.2f + 0.6f * Random01(p.Id, 5), MathF.Sin(tilt)).Normalized();
            var at = ToGodot(p.From).Lerp(ToGodot(p.Position), t) + lift + Vector3.Up * (hop * 4 * t * (1 - t));
            _pickups.Add(new Transform3D(new Basis(axis, (1 - t) * turns * MathF.Tau) * rest, at));
        }
        _pickups.End();
        SyncShards(state, _seconds);
        SyncCollecting(state, _seconds);
    }

    // A stable pseudo-random 0..1 per id and draw: the view's own, so the sim's generator stays the sim's.
    static float Random01(int id, int draw)
    {
        uint h = (uint)id * 2654435761u ^ (uint)(draw + 1) * 2246822519u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>Effects for belt events: a collected pickup flies into its unit, with a +1 over it.</summary>
    public void OnEvent(SimEvent e, SimState state)
    {
        if (e.Kind != SimEventKind.PickupCollected || !_pickupAt.Remove(e.Id, out var at)) return;
        _collecting.Add((at, e.Index, Random01(e.Id, 0) * MathF.Tau, _seconds));
        var label = _spareLabels.Count > 0 ? _spareLabels.Pop() : NewPopLabel();
        int owner = state.Units.Find(u => u.Id == e.Index).Owner;
        label.Modulate = PlayerPalette.Color(owner).Lightened(0.35f);
        label.Visible = true;
        _pops.Add((label, at + Vector3.Up * 0.8f, _seconds));
    }

    Label3D NewPopLabel()
    {
        var label = new Label3D
        {
            Text = "+1",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 56,
            PixelSize = 0.012f,
            OutlineSize = 10,
        };
        AddChild(label);
        return label;
    }

    // Collected pickups shrink into their unit; the +1 above rises and fades.
    void SyncCollecting(SimState state, float seconds)
    {
        _collecting.RemoveAll(c => seconds - c.Born > CollectSeconds || seconds < c.Born);
        _collectBatch.Begin(_collecting.Count);
        foreach (var (from, unitId, yaw, born) in _collecting)
        {
            float t = (seconds - born) / CollectSeconds;
            var to = from;
            foreach (var u in state.Units) if (u.Id == unitId) { to = ToGodot(u.Position) + Vector3.Up; break; }
            var at = from.Lerp(to, t * t) + Vector3.Up * (0.6f * 4 * t * (1 - t));
            _collectBatch.Add(new Transform3D(new Basis(Vector3.Up, yaw + t * 4).Scaled(Vector3.One * (1 - 0.8f * t)), at));
        }
        _collectBatch.End();

        for (int i = _pops.Count - 1; i >= 0; i--)
        {
            var (label, from, born) = _pops[i];
            float t = (seconds - born) / PopSeconds;
            if (t > 1 || t < 0)
            {
                label.Visible = false;
                _spareLabels.Push(label);
                _pops.RemoveAt(i);
                continue;
            }
            label.Position = from + Vector3.Up * (PopRise * (1 - (1 - t) * (1 - t)));
            label.Modulate = label.Modulate with { A = t < 0.6f ? 1 : 1 - (t - 0.6f) / 0.4f };
        }
    }

    // A smashed package the sim has dropped has landed: it bursts into shards that scatter and settle.
    void SyncShards(SimState state, float seconds)
    {
        _landed.Clear();
        foreach (var id in _smashing.Keys) _landed.Add(id);
        foreach (var p in state.Pickups) if (p.Smashed) _landed.Remove(p.Id);
        foreach (int id in _landed)
        {
            var (at, yaw) = _smashing[id];
            _smashing.Remove(id);
            for (int k = 0; k < ShardCount; k++)
            {
                float a = yaw + k * MathF.Tau / ShardCount;
                var out_ = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * ShardSpeed * (0.6f + 0.4f * ((id + k) % 3) / 2f);
                _shards.Add((at, out_ + Vector3.Up * ShardRise, new Vector3(MathF.Sin(a), 1, MathF.Cos(a)).Normalized(), seconds));
            }
        }

        _shards.RemoveAll(s => seconds - s.Born > ShardSeconds || seconds < s.Born); // gone, or the game restarted
        _shardBatch.Begin(_shards.Count);
        float floor = ShardSize / 2;
        foreach (var (from, velocity, axis, born) in _shards)
        {
            float t = seconds - born;
            var at = from + velocity * t + Vector3.Down * (Gravity * t * t / 2);
            if (at.Y < floor) at = at with { Y = floor };
            float shrink = 1 - MathF.Max(0, t / ShardSeconds - 0.6f) / 0.4f; // shrink away over the last 40%
            _shardBatch.Add(new Transform3D(new Basis(axis, t * 9f).Scaled(Vector3.One * shrink), at));
        }
        _shardBatch.End();
    }

    // Over the housing just before the open belt starts (or over the source, for a belt with no covered start).
    (HealthBar, Label3D, int) BuildGauge(BeltLine line)
    {
        int open = Array.FindIndex(line.Segments, s => !s.Covered);
        float mouth = open < 0 ? line.Length : line.Segments[open].Start;
        var at = ToGodot(line.PositionAt(MathF.Max(0, mouth - GaugeBack))) with { Y = GaugeHeight };
        var bar = new HealthBar(width: 4f, height: 0.4f) { Position = at };
        AddChild(bar);
        var label = new Label3D
        {
            Position = at + new Vector3(0, 1.1f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 64,
            PixelSize = 0.012f,
            OutlineSize = 12,
            Modulate = SupplyColor,
        };
        AddChild(label);
        return (bar, label, -1);
    }

    void SyncGauges(SimState state)
    {
        for (int l = 0; l < _gauges.Count; l++)
        {
            if (_gauges[l] is not var (bar, label, shown)) continue;
            var line = state.Belts[l];
            if (line.Reserve == shown) continue;
            bar.Set((float)line.Reserve / line.Supply, line.Reserve == 0 ? HealthBar.HealthColor(0) : SupplyColor);
            label.Text = line.Reserve == 0 ? "Source dry" : $"Supply {line.Reserve}";
            _gauges[l] = (bar, label, line.Reserve);
        }
    }

    // Tint by damage, light up on hover, buckle when broken (less as repair goes on), health bar when hurt.
    void SyncSegments(int l, BeltLine line, SegmentView[] views)
    {
        for (int s = 0; s < line.Segments.Length; s++)
        {
            var segment = line.Segments[s];
            if (segment.Covered) continue; // never damaged, never picked
            var view = views[s];
            float health = segment.Health / segment.MaxHealth;
            bool broken = segment.State == SegmentState.Broken;

            var tint = broken ? BrokenTint.Lerp(Colors.White, health * 0.5f) : Colors.White.Lerp(DamagedTint, 1 - health);
            if (view.Tint != tint) view.Material.AlbedoColor = view.Tint = tint;

            var hover = Hover.Line == l && Hover.Segment == s ? Hover.Kind : HoverKind.None;
            if (view.Hover != hover)
            {
                view.Hover = hover;
                view.Material.EmissionEnergyMultiplier = hover == HoverKind.None ? 0 : 1;
                view.Material.Emission = hover switch
                {
                    HoverKind.Attack => new Color(0.55f, 0.1f, 0.08f),
                    HoverKind.Repair => new Color(0.1f, 0.4f, 0.5f),
                    _ => new Color(0.28f, 0.3f, 0.34f),
                };
            }

            float buckle = broken ? 1 - health : -1;
            if (view.Buckle != buckle)
            {
                if (broken && view.Wreck is null) BuildWreck(line, s, view);
                view.Whole.Visible = !broken;
                if (view.Wreck is not null) view.Wreck.Visible = broken;
                if (broken) Buckle(line.Segments[s].Curve, view, buckle);
                view.Buckle = buckle;
            }

            if (health != view.HealthShown)
            {
                view.HealthShown = health;
                if (view.Health is null && health < 1)
                {
                    var curve = segment.Curve;
                    AddChild(view.Health = new HealthBar { Position = ToGodot(curve.PositionAt(curve.Length / 2)) + new Vector3(0, HealthBarHeight, 0) });
                }
                if (view.Health is not null)
                {
                    view.Health.Set(health, HealthBar.HealthColor(health));
                    view.Health.Visible = health < 1;
                }
            }
        }
    }

    // The two halves of a broken segment, each hinged at its outer end on the ground, and debris in the gap.
    void BuildWreck(BeltLine line, int s, SegmentView view)
    {
        var curve = line.Segments[s].Curve;
        float mid = curve.Length / 2;
        bool last = s == line.Segments.Length - 1;
        var wreck = new Node3D();
        AddChild(wreck);

        var startPivot = ToGodot(curve.PositionAt(0)) with { Y = 0 };
        var endPivot = ToGodot(curve.PositionAt(curve.Length)) with { Y = 0 };
        view.HalfA = new Node3D { Position = startPivot };
        view.HalfB = new Node3D { Position = endPivot };
        view.HalfA.AddChild(new MeshInstance3D { Mesh = BuildSection(curve, 0, mid - BreakGap / 2, startPivot, joint: true, endJoint: false), MaterialOverride = view.Material });
        view.HalfB.AddChild(new MeshInstance3D { Mesh = BuildSection(curve, mid + BreakGap / 2, curve.Length, endPivot, joint: false, endJoint: last), MaterialOverride = view.Material });
        wreck.AddChild(view.HalfA);
        wreck.AddChild(view.HalfB);

        // A few torn plates and bent rail pieces around the break, the same for the same segment every time.
        var center = ToGodot(curve.PositionAt(mid)) with { Y = 0 };
        var forward = ToGodot(curve.DirectionAt(mid)) with { Y = 0 };
        forward = forward.Normalized();
        var side = forward.Cross(Vector3.Up);
        var random = new RandomNumberGenerator { Seed = (ulong)(line.Segments.Length * 7919 + s * 104729 + (int)(curve.Length * 1000)) };
        for (int k = 0; k < 6; k++)
        {
            bool rail = k % 3 == 0;
            var size = rail ? new Vector3(RailWidth, RailWidth, random.RandfRange(0.5f, 0.9f)) : new Vector3(random.RandfRange(0.25f, 0.5f), 0.08f, random.RandfRange(0.2f, 0.45f));
            var at = center + side * random.RandfRange(-1.4f, 1.4f) + forward * random.RandfRange(-0.9f, 0.9f) + new Vector3(0, size.Y / 2, 0);
            var basis = new Basis(Vector3.Up, random.RandfRange(0, MathF.Tau)) * new Basis(Vector3.Right, random.RandfRange(-0.3f, 0.3f));
            wreck.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = rail ? _railDebrisMaterial : _debrisMaterial, Transform = new Transform3D(basis, at) });
        }
        view.Wreck = wreck;
    }

    // Tears the halves up at the break by `amount` (0..1 of the full buckle), each about its outer end.
    void Buckle(BezierSegment curve, SegmentView view, float amount)
    {
        float lift = Mathf.DegToRad(BuckleDegrees) * amount, twist = Mathf.DegToRad(TwistDegrees) * amount;
        var a = (ToGodot(curve.DirectionAt(0)) with { Y = 0 }).Normalized();
        var b = (ToGodot(curve.DirectionAt(curve.Length)) with { Y = 0 }).Normalized();
        // About the horizontal axis across the belt: the start half's far end, and the end half's near end, rise.
        view.HalfA.Basis = new Basis(Vector3.Up.Cross(a).Normalized(), -lift) * new Basis(a, twist);
        view.HalfB.Basis = new Basis(Vector3.Up.Cross(b).Normalized(), lift) * new Basis(b, -twist);
    }

    void SyncPosts(SimState state)
    {
        _seen.Clear();
        foreach (var gatherer in state.Gatherers)
        {
            _seen.Add(gatherer.Id);
            if (!_posts.TryGetValue(gatherer.Id, out var post)) _posts[gatherer.Id] = post = BuildPost(state, gatherer);

            bool glowing = state.Tick - gatherer.LastGrabTick < FlashTicks;
            if (post.Glowing != glowing) post.Material.EmissionEnergyMultiplier = (post.Glowing = glowing) ? 1.5f : 0f;
            float health = gatherer.Health / gatherer.MaxHealth;
            post.Health.Set(health, HealthBar.HealthColor(health));
            post.Health.Visible = health < 1;
        }

        if (_posts.Count == _seen.Count) return;
        _gone.Clear();
        foreach (int id in _posts.Keys)
            if (!_seen.Contains(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            _posts[id].Root.QueueFree();
            _posts.Remove(id);
        }
    }

    // A box in the owner's colors, with an arm reaching to its pull point on the belt.
    PostView BuildPost(SimState state, in Gatherer gatherer)
    {
        var material = (StandardMaterial3D)GathererMaterial.Duplicate();
        material.AlbedoColor = GathererMaterial.AlbedoColor.Lerp(PlayerPalette.Color(gatherer.Owner), 0.55f);

        var root = new Node3D { Position = ToGodot(gatherer.Position) };
        AddChild(root);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = PostSize },
            MaterialOverride = material,
            Position = new Vector3(0, PostSize.Y / 2, 0),
        });

        var pull = ToGodot(state.Belts[gatherer.Line].PositionAt(gatherer.Distance)) - root.Position;
        var from = new Vector3(0, pull.Y + 0.1f, 0);
        var reach = pull - from;
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.12f, reach.Length()) },
            MaterialOverride = material,
            Transform = new Transform3D(Basis.LookingAt(reach, Vector3.Up), from + reach / 2),
        });

        var health = new HealthBar { Position = new Vector3(0, PostSize.Y + 0.6f, 0), Visible = false };
        root.AddChild(health);
        return new PostView(root, material, health);
    }

    // A stretch of belt from `from` to `to` m along the curve, its vertices relative to `origin`: the
    // cross-section swept along it (base walls, rails, the surface between), capped at both ends, with a
    // joint crossbar at its start and, if asked, at its end. Colors are in the vertices.
    ArrayMesh BuildSection(BezierSegment curve, float from, float to, Vector3 origin, bool joint, bool endJoint, bool covered = false)
    {
        float hw = BeltWidth / 2, r = RailWidth, rh = RailHeight;
        int steps = Math.Max(1, (int)MathF.Ceiling((to - from) / RibbonStep));
        var centers = new Vector3[steps + 1];
        var sides = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float d = from + (to - from) * i / steps;
            centers[i] = ToGodot(curve.PositionAt(d)) - origin;
            sides[i] = ToGodot(curve.DirectionAt(d)).Cross(Vector3.Up).Normalized();
        }
        // A point of the cross-section at step i: `o` across (toward `sides`), `h` above the surface, or on
        // the ground for h = Ground.
        const float Ground = float.NegativeInfinity;
        Vector3 At(int i, float o, float h) => centers[i] + sides[i] * o + Vector3.Up * (h == Ground ? -(centers[i].Y + origin.Y) : h);

        // The cross-section, left to right; each consecutive pair is swept into a strip, colored as the
        // second point says, facing out of the belt.
        float w = hw + r + HousingOverhang;
        (float O, float H, Color C)[] profile = covered
            ? [(-w, Ground, HousingColor), (-w, HousingHeight, HousingColor), (w, HousingHeight, HousingRoofColor), (w, Ground, HousingColor)]
            : [
                (-hw - r, Ground, BaseColor), (-hw - r, rh, BaseColor), (-hw, rh, RailColor), (-hw, 0, RailColor),
                (hw, 0, BeltMaterial.AlbedoColor), (hw, rh, RailColor), (hw + r, rh, RailColor), (hw + r, Ground, BaseColor),
            ];
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int e = 0; e + 1 < profile.Length; e++)
        {
            var (o0, h0, _) = profile[e];
            var (o1, h1, color) = profile[e + 1];
            // Out of the belt: across the edge, to its left as it runs left to right (up, for the surface).
            float eo = o1 - o0, eh = (h1 == Ground ? -1 : h1) - (h0 == Ground ? -1 : h0);
            for (int i = 0; i < steps; i++)
            {
                var outward = sides[i] * -eh + Vector3.Up * eo;
                Quad(st, At(i, o0, h0), At(i, o1, h1), At(i + 1, o1, h1), At(i + 1, o0, h0), outward, color);
            }
        }
        var back = -(centers[1] - centers[0]);
        var ahead = centers[steps] - centers[steps - 1];
        if (covered)
        {
            // Closed at both ends, dark: where the open belt goes in or comes out, it reads as a tunnel mouth.
            var mouth = new Color(0.08f, 0.08f, 0.09f);
            Quad(st, At(0, -w, Ground), At(0, w, Ground), At(0, w, HousingHeight), At(0, -w, HousingHeight), back, mouth);
            Quad(st, At(steps, -w, Ground), At(steps, w, Ground), At(steps, w, HousingHeight), At(steps, -w, HousingHeight), ahead, mouth);
            return st.Commit();
        }
        Cap(st, (o, h) => At(0, o, h), back, hw, r, rh, Ground);
        Cap(st, (o, h) => At(steps, o, h), ahead, hw, r, rh, Ground);

        if (joint) Joint(st, centers[0], sides[0], -(centers[0].Y + origin.Y));
        if (endJoint) Joint(st, centers[steps], sides[steps], -(centers[steps].Y + origin.Y));
        return st.Commit();
    }

    // The end of a stretch: its cross-section filled in (the base and each rail), dark, facing `outward`.
    static void Cap(SurfaceTool st, Func<float, float, Vector3> at, Vector3 outward, float hw, float r, float rh, float ground)
    {
        var color = new Color(0.16f, 0.16f, 0.17f);
        float edge = hw + r;
        Quad(st, at(-edge, ground), at(edge, ground), at(edge, 0), at(-edge, 0), outward, color); // under the surface
        Quad(st, at(-edge, 0), at(-hw, 0), at(-hw, rh), at(-edge, rh), outward, color);          // the left rail
        Quad(st, at(hw, 0), at(edge, 0), at(edge, rh), at(hw, rh), outward, color);              // the right rail
    }

    // A crossbar across the belt at a joint, from the ground to just above the rails.
    void Joint(SurfaceTool st, Vector3 center, Vector3 side, float ground)
    {
        var forward = Vector3.Up.Cross(side).Normalized(); // along the belt
        float half = BeltWidth / 2 + RailWidth + JointOverhang, top = RailHeight + JointRise, length = JointLength / 2;
        Vector3 P(float o, float h, float f) => center + side * o + Vector3.Up * h + forward * f;
        var c = JointColor;
        Quad(st, P(-half, top, -length), P(half, top, -length), P(half, top, length), P(-half, top, length), Vector3.Up, c);
        Quad(st, P(-half, ground, -length), P(half, ground, -length), P(half, top, -length), P(-half, top, -length), -forward, c);
        Quad(st, P(-half, ground, length), P(half, ground, length), P(half, top, length), P(-half, top, length), forward, c);
        Quad(st, P(-half, ground, -length), P(-half, ground, length), P(-half, top, length), P(-half, top, -length), -side, c);
        Quad(st, P(half, ground, -length), P(half, ground, length), P(half, top, length), P(half, top, -length), side, c);
    }

    // A quad a-b-c-d (in order around it) facing `outward`: wound clockwise as seen from that side, which is
    // Godot's front face, with a flat normal.
    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color)
    {
        if ((b - a).Cross(d - a).Dot(outward) > 0) (b, d) = (d, b);
        var normal = -(b - a).Cross(d - a);
        st.SetColor(color);
        st.SetNormal(normal.LengthSquared() > 1e-12f ? normal.Normalized() : Vector3.Up);
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }
}
