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
/// being placed, green strips beside the belts mark where one can go (ShowPostSpots).
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
    readonly Dictionary<int, PostView> _posts = []; // by gatherer id; destroyed posts go away
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    InstanceBatch _packages = null!, _pickups = null!;
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
        }

        var box = new BoxMesh { Size = Vector3.One * PackageSize, Material = PackageMaterial };
        _packages = new InstanceBatch(this, box);
        _pickups = new InstanceBatch(this, box);
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

        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            float yaw = p.Id * 2.4f; // golden-angle steps: a stable, scattered look per pickup
            _pickups.Add(ToGodot(p.Position) + lift, new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)));
        }
        _pickups.End();
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
