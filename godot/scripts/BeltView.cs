using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws the supply routes (belt lines in the code): roads, the trucks on them, spilled pickups, and
/// depots (gatherer posts). A road is a flat strip on the ground with light edges and a seam where each
/// piece meets the next, so every piece is visibly a thing that can be shot apart. Damage tints a piece
/// and shows its health bar; a broken one is torn slabs round a crater, flattening back as repair brings
/// its health back. The piece under the cursor, with units selected, lights up in the color of what a
/// right-click would do (PlayerInput sets Hover). Covered stretches are a closed housing over the road,
/// dark where the open road comes out; trucks inside it aren't drawn. A truck is a cab and a bed with
/// crates on it, as many as its load left; hurt, it shows a health bar, and destroyed it leaves a burnt
/// wreck that sinks away. While a depot is being placed, green strips beside the roads mark where one
/// can go (ShowPostSpots). A spilled package jumps off the truck tumbling and lands; one that breaks in the
/// fall bursts into shards. A finite source shows what it has left as a gauge on the housing where its
/// road comes into play. Under fog of war, trucks, spills and enemy depots show only where the local player
/// sees, and road pieces as it last saw them (Sight); a remembered enemy depot stays until it looks again.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between belt cross-sections
    const float FlashTicks = 6;     // a gatherer glows this long after a grab
    static readonly Vector3 PostSize = new(2f, 1.6f, 2f); // a depot's shed
    // A truck, m: its body from the ground up, the cab at the front; crates stand on the bed in rows of
    // two, three rows deep. A wreck stays this long, sinking over its last seconds.
    const float TruckLength = 6f, TruckWidth = 2.3f, CabLength = 1.8f, CabHeight = 2.4f, BedHeight = 1.1f, ChassisHeight = 0.7f;
    const int CrateSpots = 6;
    const float CrateSize = 0.8f;
    const float WreckSeconds = 30f, WreckSink = 5f;
    const float RoadTop = 0.05f, EdgeWidth = 0.22f; // the road's surface above the ground; the width of debris from its edge
    const float LineInset = 0.18f, LineWidth = 0.12f;         // m: the painted edge lines, in from the road's edge
    const float ShoulderWidth = 0.7f, ShoulderTop = 0.015f;   // m: the gravel verge either side, just above the ground
    const float DashLength = 1.25f, DashPeriod = 2.5f, DashWidth = 0.13f; // the dashed center line (a road piece is 5 m: whole periods)

    // The belt's cross-section, m: rails either side of the surface, standing above it, and a base down
    // to the ground. Joints are crossbars a little wider and taller than the belt.
    const float RailWidth = 0.14f, RailHeight = 0.16f;
    const float JointLength = 0.3f, JointOverhang = 0.15f, JointRise = 0.06f;
    const float BreakGap = 2.4f;     // m between a broken piece's halves: the crater
    const float BuckleDegrees = 7f;  // how far a broken piece's slabs tilt up at the crater, at 0 health
    const float TwistDegrees = 5f;   // and roll sideways, opposite ways, so it reads as wreckage
    const float HealthBarHeight = 1.4f;
    const float HousingHeight = 2.8f, HousingOverhang = 0.3f; // m above the road, and beyond its edges: a truck fits under
    const float BaseDepth = 0.3f;                              // m: how far a housing reaches below the road
    const float SpotWidth = 0.5f, SpotHeight = 0.04f, SpotStep = 0.5f; // the free-spot strips beside the belt
    const float FallHop = 0.9f, FallTurns = 1.25f; // a spilled package's jump off the belt, on average (its time is the sim's)
    const float CollectSeconds = 0.35f, PopSeconds = 1.1f, PopRise = 2.2f; // a collected pickup flies to its unit; the +1 floats up
    const int ShardCount = 5;
    const float ShardSize = 0.26f, ShardSpeed = 2.6f, ShardRise = 2.4f, ShardSeconds = 0.9f, Gravity = 9.8f;
    // The textures (placeholders, built by tools/make_textures.py); without them the belt and packages are
    // flat colors. The belt atlas is horizontal bands, each one tile across its whole width (rows / 1024).
    const string BeltAtlasPath = "res://assets/textures/belt_atlas.png", CratesPath = "res://assets/textures/package_crates.png";
    static readonly Vector2 BandTop = new(8, 448), BandRail = new(464, 520), BandCrossbar = new(536, 592), BandHousing = new(608, 1016);
    const float TopRepeat = 2.72f, RailRepeat = 2f, HousingRepeat = 0.85f; // m of belt per tile of each band
    const int CrateKinds = 12;
    const string CrateShader = @"
shader_type spatial;
uniform sampler2D crates : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
varying flat float kind;
varying flat vec2 face_cell;
void vertex() {
    kind = INSTANCE_CUSTOM.r;
    // A box's UVs put its six faces in a 3 x 2 grid (+z, +x, -z; -x, +y, -y). Which cell is this face's
    // comes from its normal: a face's edge UVs sit on the cell border, so they can't tell.
    vec3 n = NORMAL;
    face_cell = abs(n.y) > 0.5 ? vec2(n.y > 0.0 ? 1.0 : 2.0, 1.0)
              : abs(n.x) > 0.5 ? vec2(n.x > 0.0 ? 1.0 : 0.0, n.x > 0.0 ? 0.0 : 1.0)
              : vec2(n.z > 0.0 ? 0.0 : 2.0, 0.0);
}
void fragment() {
    // Each face shows one crate of the 4 x 3 atlas.
    vec2 face = clamp(UV * vec2(3.0, 2.0) - face_cell, 0.0, 1.0);
    vec2 cell = vec2(mod(kind, 4.0), floor(kind / 4.0));
    ALBEDO = texture(crates, (cell + face) / vec2(4.0, 3.0)).rgb;
    ROUGHNESS = 0.85;
}";
    // A segment's look: its vertex colors (authored like albedo colors, so sRGB) times the atlas, tinted by
    // damage, glowing on hover. The surface band scrolls at the belt's speed, on sim time, so it keeps pace
    // with the packages at any game speed and stops with the game.
    const string TimeParameter = "belt_time";
    const string SegmentShader = @"
shader_type spatial;
global uniform float belt_time;
uniform sampler2D atlas : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool textured = false;
uniform vec4 tint : source_color = vec4(1.0);
uniform vec3 glow : source_color = vec3(0.0);
uniform float glow_energy = 0.0;
uniform float scroll = 0.0; // surface tiles per second
uniform vec2 surface_band;  // the surface's rows of the atlas, as v
uniform float roughness = 0.8;
vec3 linear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
void fragment() {
    vec2 uv = UV;
    if (uv.y >= surface_band.x && uv.y <= surface_band.y) uv.x -= belt_time * scroll;
    vec3 albedo = linear(COLOR.rgb) * tint.rgb;
    if (textured) albedo *= texture(atlas, uv).rgb;
    ALBEDO = albedo;
    ROUGHNESS = roughness;
    EMISSION = glow * glow_energy;
}";
    const float GaugeBack = 2f, GaugeRise = 1.6f; // m: back from where the open belt starts, and above the belt

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
    [Export] public float BeltWidth = 3.2f; // the road's
    [Export] public Color RoadColor = new(0.17f, 0.17f, 0.18f);       // asphalt
    [Export] public Color RoadEdgeColor = new(0.8f, 0.8f, 0.76f);     // the painted edge lines
    [Export] public Color CenterLineColor = new(0.85f, 0.68f, 0.22f); // the dashed line down the middle
    [Export] public Color ShoulderColor = new(0.33f, 0.31f, 0.27f);   // the gravel verge
    [Export] public Color TruckColor = new(0.45f, 0.43f, 0.33f);   // nobody's: a drab olive
    [Export] public Color CabColor = new(0.33f, 0.34f, 0.3f);
    [Export] public Color WreckColor = new(0.1f, 0.09f, 0.08f);
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
        public ShaderMaterial Material = null!;
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
    readonly List<(Vector3 From, Vector3 Velocity, Vector3 Axis, float Born, float Crate)> _shards = [];
    readonly Dictionary<int, Vector3> _pickupAt = []; // where each pickup was last drawn, for its collection
    readonly List<(Vector3 From, int Unit, float Yaw, float Born, float Crate)> _collecting = [];
    readonly List<(Label3D Label, Vector3 From, float Born)> _pops = [];
    readonly Stack<Label3D> _spareLabels = [];
    float _seconds; // sim time as last drawn
    readonly Dictionary<int, PostView> _posts = []; // by gatherer id; destroyed posts go away
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    InstanceBatch _packages = null!, _cargo = null!, _pickups = null!, _shardBatch = null!, _collectBatch = null!;
    readonly Dictionary<int, HealthBar> _truckBars = []; // trucks that are hurt, by id
    readonly Dictionary<int, Transform3D> _truckAt = []; // where each truck was last drawn, for its wreck
    readonly List<(Node3D Node, float Born)> _wrecks = [];
    StandardMaterial3D _wreckMaterial = null!;
    static bool _timeParameterAdded;
    bool _textured; // the belt atlas loaded: parts take their look from it, not their vertex colors
    StandardMaterial3D _debrisMaterial = null!, _railDebrisMaterial = null!;
    StandardMaterial3D? _craterMaterial;
    MeshInstance3D _spots = null!;

    public void Build(SimState state)
    {
        var atlas = ResourceLoader.Exists(BeltAtlasPath) ? GD.Load<Texture2D>(BeltAtlasPath) : null;
        _textured = atlas is not null;
        if (!_timeParameterAdded) // once per run: it outlives the scene, across restarts
        {
            RenderingServer.GlobalShaderParameterAdd(TimeParameter, RenderingServer.GlobalShaderParameterType.Float, 0f);
            _timeParameterAdded = true;
        }
        var shader = new Shader { Code = SegmentShader };
        _debrisMaterial = new StandardMaterial3D { AlbedoColor = DebrisColor, Roughness = 0.9f };
        _railDebrisMaterial = new StandardMaterial3D { AlbedoColor = RoadEdgeColor * BrokenTint, Roughness = 0.9f };
        foreach (var line in state.Belts)
        {
            var views = new SegmentView[line.Segments.Length];
            for (int s = 0; s < line.Segments.Length; s++)
            {
                bool covered = line.Segments[s].Covered;
                var material = new ShaderMaterial { Shader = shader };
                material.SetShaderParameter("atlas", atlas!);
                material.SetShaderParameter("textured", _textured && covered); // the road is flat colors; only its housing is textured
                material.SetShaderParameter("roughness", 0.9f);
                material.SetShaderParameter("surface_band", BandTop / 1024f);
                material.SetShaderParameter("scroll", 0f);
                var curve = line.Segments[s].Curve;
                var whole = new MeshInstance3D { Mesh = BuildSection(curve, 0, curve.Length, Vector3.Zero, covered), MaterialOverride = material };
                AddChild(whole);
                views[s] = new SegmentView { Whole = whole, Material = material };
            }
            _segments.Add(views);
            _gauges.Add(line.Finite ? BuildGauge(line) : null);
        }

        var crates = PackageMaterial;
        if (ResourceLoader.Exists(CratesPath))
        {
            var crateMaterial = new ShaderMaterial { Shader = new Shader { Code = CrateShader } };
            crateMaterial.SetShaderParameter("crates", GD.Load<Texture2D>(CratesPath));
            crates = crateMaterial;
        }
        var box = new BoxMesh { Size = Vector3.One * PackageSize, Material = crates };
        var body = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.75f };
        _packages = new InstanceBatch(this, BuildTruck(body));
        _cargo = new InstanceBatch(this, new BoxMesh { Size = Vector3.One * CrateSize, Material = crates }, customData: true);
        _wreckMaterial = new StandardMaterial3D { AlbedoColor = WreckColor, Roughness = 1f };
        _pickups = new InstanceBatch(this, box, customData: true);
        _shardBatch = new InstanceBatch(this, new BoxMesh { Size = Vector3.One * ShardSize, Material = crates }, customData: true);
        _collectBatch = new InstanceBatch(this, box, customData: true);
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
                        return (ToGodot(line.PositionAt(d)) with { Y = RoadTop + SpotHeight }) + side * (sign * (Simulation.PostOffset + o));
                    }
                    Quad(st, Edge(i, -SpotWidth / 2), Edge(i, SpotWidth / 2), Edge(i + 1, SpotWidth / 2), Edge(i + 1, -SpotWidth / 2), Vector3.Up, Colors.White);
                }
        }
        _spots.Mesh = st.Commit();
    }

    SimState _state = null!;

    public void Sync(SimState state, float alpha)
    {
        _state = state;
        RenderingServer.GlobalShaderParameterSet(TimeParameter, (state.Tick - 1 + alpha) / Simulation.TicksPerSecond);
        var lift = new Vector3(0, PackageSize / 2, 0); // sit on the ground, not in it

        int total = 0;
        foreach (var line in state.Belts) total += line.Packages.Count;
        _packages.Begin(total);
        _cargo.Begin(total * CrateSpots);
        _seen.Clear();
        for (int l = 0; l < state.Belts.Count; l++)
        {
            var line = state.Belts[l];
            SyncSegments(l, line, _segments[l]);
            foreach (var p in line.Packages)
            {
                if (line.Segments[p.Segment].Covered || !Sight.SeesAt(p.Position)) continue; // in the tunnel, or out of sight
                var at = ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) with { Y = 0 };
                var forward = ToGodot(p.Direction) with { Y = 0 };
                var basis = Basis.LookingAt(forward.LengthSquared() > 1e-6f ? forward : Vector3.Forward, Vector3.Up);
                var truck = new Transform3D(basis, at);
                _packages.Add(truck);
                _truckAt[p.Id] = truck;
                _seen.Add(p.Id);
                // Crates on the bed: as many of the spots as its load left fills, front row first.
                int crates = (int)MathF.Ceiling(CrateSpots * (float)p.Cargo / line.Load);
                for (int k = 0; k < crates; k++)
                {
                    var spot = new Vector3((k % 2 - 0.5f) * (CrateSize + 0.1f), BedHeight + CrateSize / 2, -TruckLength / 2 + CabLength + 0.6f + k / 2 * (CrateSize + 0.15f));
                    _cargo.Add(new Transform3D(basis, at + basis * new Vector3(spot.X, spot.Y, -spot.Z)), Crate(p.Id * 8 + k));
                }
                SyncTruckBar(p, at);
            }
        }
        _packages.End();
        _cargo.End();
        ForgetTrucks();
        SyncWrecks((state.Tick - 1 + alpha) / Simulation.TicksPerSecond);

        SyncPosts(state);
        SyncGauges(state);

        // A spilled package jumps off the belt, tumbling, and lands at rest where the sim put it. Each
        // one's hop, tumble axis and spin are its own, from its id.
        float now = state.Tick - 1 + alpha; // the tick being drawn, as positions interpolate
        _seconds = now / Simulation.TicksPerSecond;
        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            if (!Sight.SeesAt(p.Position)) continue;
            float yaw = Random01(p.Id, 0) * MathF.Tau;
            var rest = new Basis(Vector3.Up, yaw);
            float t = Math.Clamp((now - p.SpilledAtTick) / (p.LandsAtTick - p.SpilledAtTick), 0, 1);
            if (p.Smashed) _smashing[p.Id] = (ToGodot(p.Position) + lift, yaw);
            float crate = Crate(p.Id);
            _pickupAt[p.Id] = ToGodot(p.Position) + lift;
            if (t >= 1)
            {
                _pickups.Add(new Transform3D(rest, ToGodot(p.Position) + lift), crate);
                continue;
            }
            float hop = FallHop * (0.7f + 0.6f * Random01(p.Id, 1));
            float turns = FallTurns * (0.5f + Random01(p.Id, 2)) * (Random01(p.Id, 3) < 0.5f ? -1 : 1);
            float tilt = Random01(p.Id, 4) * MathF.Tau;
            var axis = new Vector3(MathF.Cos(tilt), 0.2f + 0.6f * Random01(p.Id, 5), MathF.Sin(tilt)).Normalized();
            // Up first, then out, so it clears the rail before it swings wide of the belt.
            var from = ToGodot(p.From) with { Y = BedHeight }; // off the truck's bed
            var at = from.Lerp(ToGodot(p.Position), t * t) + lift + Vector3.Up * (hop * 4 * t * (1 - t));
            _pickups.Add(new Transform3D(new Basis(axis, (1 - t) * turns * MathF.Tau) * rest, at), crate);
        }
        _pickups.End();
        SyncShards(state, _seconds);
        SyncCollecting(state, _seconds);
    }

    // Which crate a package looks like, as the shader's custom value: its own, whether on the belt or spilled.
    static float Crate(int id) => MathF.Floor(Random01(id, 7) * CrateKinds);

    // A stable pseudo-random 0..1 per id and draw: the view's own, so the sim's generator stays the sim's.
    static float Random01(int id, int draw)
    {
        uint h = (uint)id * 2654435761u ^ (uint)(draw + 1) * 2246822519u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>Effects for route events: a destroyed truck leaves a wreck; a collected pickup flies into its unit, with a +1 over it.</summary>
    public void OnEvent(SimEvent e, SimState state)
    {
        if (e.Kind == SimEventKind.TruckDestroyed)
        {
            if (_truckAt.Remove(e.Id, out var where) && Sight.SeesAt(ToSim(where.Origin))) AddWreck(where);
            return;
        }
        if (e.Kind != SimEventKind.PickupCollected || !_pickupAt.Remove(e.Id, out var at) || !Sight.SeesAt(ToSim(at))) return;
        _collecting.Add((at, e.Index, Random01(e.Id, 0) * MathF.Tau, _seconds, Crate(e.Id)));
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
        foreach (var (from, unitId, yaw, born, crate) in _collecting)
        {
            float t = (seconds - born) / CollectSeconds;
            var to = from;
            foreach (var u in state.Units) if (u.Id == unitId) { to = ToGodot(u.Position) + Vector3.Up; break; }
            var at = from.Lerp(to, t * t) + Vector3.Up * (0.6f * 4 * t * (1 - t));
            _collectBatch.Add(new Transform3D(new Basis(Vector3.Up, yaw + t * 4).Scaled(Vector3.One * (1 - 0.8f * t)), at), crate);
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
                _shards.Add((at, out_ + Vector3.Up * ShardRise, new Vector3(MathF.Sin(a), 1, MathF.Cos(a)).Normalized(), seconds, Crate(id)));
            }
        }

        _shards.RemoveAll(s => seconds - s.Born > ShardSeconds || seconds < s.Born); // gone, or the game restarted
        _shardBatch.Begin(_shards.Count);
        float floor = ShardSize / 2;
        foreach (var (from, velocity, axis, born, crate) in _shards)
        {
            float t = seconds - born;
            var at = from + velocity * t + Vector3.Down * (Gravity * t * t / 2);
            if (at.Y < floor) at = at with { Y = floor };
            float shrink = 1 - MathF.Max(0, t / ShardSeconds - 0.6f) / 0.4f; // shrink away over the last 40%
            _shardBatch.Add(new Transform3D(new Basis(axis, t * 9f).Scaled(Vector3.One * shrink), at), crate);
        }
        _shardBatch.End();
    }

    // A truck's body, facing -Z (Godot's forward), on the ground: chassis, cab at the front, the bed behind
    // it, and dark wheels. One mesh for every truck, colored in its vertices.
    ArrayMesh BuildTruck(Material material)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float hw = TruckWidth / 2, front = -TruckLength / 2, back = TruckLength / 2;
        var wheel = new Color(0.08f, 0.08f, 0.08f);
        Vector2 uv = PlainUv;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, Color c) =>
            Block(st, Vector3.Zero, Vector3.Right, Vector3.Back, x0, x1, y0, y1, z0, z1, c, uv, uv, uv, uv);
        Box(-hw + 0.15f, hw - 0.15f, 0.35f, ChassisHeight, front + 0.2f, back - 0.1f, wheel);          // chassis
        Box(-hw, hw, ChassisHeight, CabHeight, front, front + CabLength, CabColor);                    // cab
        Box(-hw, hw, ChassisHeight, BedHeight, front + CabLength + 0.1f, back, TruckColor);            // bed
        Box(-hw, -hw + 0.1f, BedHeight, BedHeight + 0.4f, front + CabLength + 0.1f, back, TruckColor); // its low sides
        Box(hw - 0.1f, hw, BedHeight, BedHeight + 0.4f, front + CabLength + 0.1f, back, TruckColor);
        Box(-hw + 0.2f, hw - 0.2f, CabHeight - 0.9f, CabHeight - 0.35f, front - 0.01f, front + 0.02f, new Color(0.15f, 0.2f, 0.25f)); // windscreen
        foreach (float z in new[] { front + 0.9f, back - 1.6f, back - 0.6f })
            foreach (float x in new[] { -hw, hw - 0.35f })
                Box(x, x + 0.35f, 0, 0.8f, z - 0.4f, z + 0.4f, wheel);
        st.SetMaterial(material);
        return st.Commit();
    }

    // A hurt truck's health bar, over its cab; made when it's first hurt, dropped when it's gone.
    void SyncTruckBar(in Package truck, Vector3 at)
    {
        float health = truck.MaxHealth > 0 ? truck.Health / truck.MaxHealth : 1;
        if (health >= 1 && !_truckBars.ContainsKey(truck.Id)) return;
        if (!_truckBars.TryGetValue(truck.Id, out var bar)) AddChild(_truckBars[truck.Id] = bar = new HealthBar());
        bar.Position = at + new Vector3(0, CabHeight + 0.8f, 0);
        bar.Set(health, HealthBar.HealthColor(health));
        bar.Visible = true;
    }

    // Trucks not drawn this frame (gone, out of sight, in a tunnel) lose their bar; the gone ones their place.
    void ForgetTrucks()
    {
        _gone.Clear();
        foreach (var id in _truckBars.Keys) if (!_seen.Contains(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            _truckBars[id].QueueFree();
            _truckBars.Remove(id);
        }
        if (_truckAt.Count <= _seen.Count + 16) return; // prune now and then, not every frame
        _gone.Clear();
        foreach (var id in _truckAt.Keys) if (!_seen.Contains(id)) _gone.Add(id);
        foreach (int id in _gone) _truckAt.Remove(id);
    }

    // A burnt-out truck where one was destroyed: its shape, charred, tipped a little, sinking away at the end.
    void AddWreck(Transform3D at)
    {
        var node = new Node3D { Transform = at };
        node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(TruckWidth, BedHeight, TruckLength - 0.4f) }, MaterialOverride = _wreckMaterial, Position = new Vector3(0, BedHeight / 2, 0), RotationDegrees = new Vector3(0, 0, 6) });
        node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(TruckWidth - 0.2f, CabHeight - BedHeight, CabLength) }, MaterialOverride = _wreckMaterial, Position = new Vector3(0, (CabHeight + BedHeight) / 2 - 0.2f, -TruckLength / 2 + CabLength / 2) });
        AddChild(node);
        _wrecks.Add((node, _seconds));
    }

    void SyncWrecks(float seconds)
    {
        for (int i = _wrecks.Count - 1; i >= 0; i--)
        {
            var (node, born) = _wrecks[i];
            float age = seconds - born;
            if (age > WreckSeconds || age < 0) // gone, or the game restarted
            {
                node.QueueFree();
                _wrecks.RemoveAt(i);
                continue;
            }
            float sink = MathF.Max(0, age - (WreckSeconds - WreckSink)) / WreckSink;
            node.Position = node.Position with { Y = -sink * CabHeight };
        }
    }

    // Over the housing just before the open road starts (or over the source, for a route with no covered start).
    (HealthBar, Label3D, int) BuildGauge(BeltLine line)
    {
        int open = Array.FindIndex(line.Segments, s => !s.Covered);
        float mouth = open < 0 ? line.Length : line.Segments[open].Start;
        var at = ToGodot(line.PositionAt(MathF.Max(0, mouth - GaugeBack))) + new Vector3(0, GaugeRise, 0);
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
            label.Text = line.Reserve == 0 ? L.T("gauge.dry") : L.T("gauge.supply", line.Reserve);
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
            float health = Sight.SeenHealth(_state, l, s) / segment.MaxHealth; // as the local player last saw it
            bool broken = Sight.SeenState(_state, l, s) == SegmentState.Broken;

            var tint = broken ? BrokenTint.Lerp(Colors.White, health * 0.5f) : Colors.White.Lerp(DamagedTint, 1 - health);
            if (view.Tint != tint) view.Material.SetShaderParameter("tint", view.Tint = tint);

            var hover = Hover.Line == l && Hover.Segment == s ? Hover.Kind : HoverKind.None;
            if (view.Hover != hover)
            {
                view.Hover = hover;
                view.Material.SetShaderParameter("glow_energy", hover == HoverKind.None ? 0f : 1f);
                view.Material.SetShaderParameter("glow", hover switch
                {
                    HoverKind.Attack => new Color(0.55f, 0.1f, 0.08f),
                    HoverKind.Repair => new Color(0.1f, 0.4f, 0.5f),
                    _ => new Color(0.28f, 0.3f, 0.34f),
                });
            }

            float buckle = broken ? 1 - health : -1;
            if (view.Buckle != buckle)
            {
                if (broken && view.Wreck is null) BuildWreck(line, s, view);
                view.Whole.Visible = !broken;
                if (view.Wreck is not null) view.Wreck.Visible = broken;
                if (broken) Buckle(line.Segments[s].Curve, view, buckle);
                if ((view.Buckle < 0) != (buckle < 0)) view.Material.SetShaderParameter("scroll", broken ? 0f : line.Speed / TopRepeat); // a wreck stands still
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
        var wreck = new Node3D();
        AddChild(wreck);

        var startPivot = ToGodot(curve.PositionAt(0));                // on the legs at either end
        var endPivot = ToGodot(curve.PositionAt(curve.Length));
        view.HalfA = new Node3D { Position = startPivot };
        view.HalfB = new Node3D { Position = endPivot };
        view.HalfA.AddChild(new MeshInstance3D { Mesh = BuildSection(curve, 0, mid - BreakGap / 2, startPivot), MaterialOverride = view.Material });
        view.HalfB.AddChild(new MeshInstance3D { Mesh = BuildSection(curve, mid + BreakGap / 2, curve.Length, endPivot), MaterialOverride = view.Material });
        wreck.AddChild(view.HalfA);
        wreck.AddChild(view.HalfB);

        // A crater in the gap, and chunks of road thrown round it, the same for the same piece every time.
        var center = ToGodot(curve.PositionAt(mid)) with { Y = 0 };
        wreck.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = BreakGap * 0.75f, BottomRadius = BreakGap * 0.5f, Height = 0.06f }, MaterialOverride = _craterMaterial ??= new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.06f, 0.05f), Roughness = 1f }, Position = center + new Vector3(0, 0.03f, 0) });
        var forward = ToGodot(curve.DirectionAt(mid)) with { Y = 0 };
        forward = forward.Normalized();
        var side = forward.Cross(Vector3.Up);
        var random = new RandomNumberGenerator { Seed = (ulong)(line.Segments.Length * 7919 + s * 104729 + (int)(curve.Length * 1000)) };
        for (int k = 0; k < 6; k++)
        {
            bool rail = k % 3 == 0; // an edge piece
            var size = rail ? new Vector3(EdgeWidth, 0.12f, random.RandfRange(0.5f, 0.9f)) : new Vector3(random.RandfRange(0.35f, 0.7f), 0.12f, random.RandfRange(0.3f, 0.6f));
            var at = center + side * random.RandfRange(-2.4f, 2.4f) + forward * random.RandfRange(-1.6f, 1.6f) + new Vector3(0, size.Y / 2, 0);
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
        // About the horizontal axis across the belt, at the legs: the start half's far end, and the end half's
        // near end, sag.
        view.HalfA.Basis = new Basis(Vector3.Up.Cross(a).Normalized(), lift) * new Basis(a, twist);
        view.HalfB.Basis = new Basis(Vector3.Up.Cross(b).Normalized(), -lift) * new Basis(b, -twist);
    }

    void SyncPosts(SimState state)
    {
        _seen.Clear();
        foreach (var gatherer in state.Gatherers)
        {
            _seen.Add(gatherer.Id);
            if (!_posts.TryGetValue(gatherer.Id, out var post)) _posts[gatherer.Id] = post = BuildPost(state, gatherer);
            if (!Sight.SeesStructure(gatherer.Owner, gatherer.Position, 1))
            {
                ShowRemembered(gatherer.Id, post);
                continue;
            }
            post.Root.Visible = true;

            bool glowing = state.Tick - gatherer.LastGrabTick < FlashTicks;
            if (post.Glowing != glowing) post.Material.EmissionEnergyMultiplier = (post.Glowing = glowing) ? 1.5f : 0f;
            float health = gatherer.Health / gatherer.MaxHealth;
            post.Health.Set(health, HealthBar.HealthColor(health));
            post.Health.Visible = health < 1;
        }

        if (_posts.Count == _seen.Count) return;
        _gone.Clear();
        foreach (var (id, post) in _posts)
            if (!_seen.Contains(id) && !ShowRemembered(id, post)) _gone.Add(id); // a gone one stays while remembered
        foreach (int id in _gone)
        {
            _posts[id].Root.QueueFree();
            _posts.Remove(id);
        }
    }

    // An enemy post out of sight: shown, unlit, if the local player remembers it. False if not.
    static bool ShowRemembered(int id, PostView post)
    {
        bool remembered = Sight.Remembers(id, out _);
        post.Root.Visible = remembered;
        post.Health.Visible = false;
        if (post.Glowing) (post.Glowing, post.Material.EmissionEnergyMultiplier) = (false, 0f);
        return remembered;
    }

    // A depot: a shed in the owner's colors, and a loading bay from it to the road's edge at its pull point.
    PostView BuildPost(SimState state, in Gatherer gatherer)
    {
        var material = (StandardMaterial3D)GathererMaterial.Duplicate();
        material.AlbedoColor = GathererMaterial.AlbedoColor.Lerp(PlayerPalette.Color(gatherer.Owner), 0.55f);

        var root = new Node3D { Position = ToGodot(gatherer.Position) with { Y = 0 } };
        AddChild(root);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = PostSize },
            MaterialOverride = material,
            Position = new Vector3(0, PostSize.Y / 2, 0),
        });

        var pull = (ToGodot(state.Belts[gatherer.Line].PositionAt(gatherer.Distance)) with { Y = 0 }) - root.Position;
        var toRoad = pull.Normalized() * MathF.Max(0.1f, pull.Length() - BeltWidth / 2); // stop at the road's edge
        var bay = toRoad - pull.Normalized() * (PostSize.X / 2);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(PostSize.X * 0.8f, 0.3f, MathF.Max(0.1f, bay.Length())) },
            MaterialOverride = material,
            Transform = new Transform3D(Basis.LookingAt(pull, Vector3.Up), pull.Normalized() * (PostSize.X / 2) + bay / 2 + new Vector3(0, 0.15f, 0)),
        });

        var health = new HealthBar { Position = new Vector3(0, PostSize.Y + 0.8f, 0), Visible = false };
        root.AddChild(health);
        return new PostView(root, material, health);
    }

    // A stretch of road from `from` to `to` m along the curve, its vertices relative to `origin`: the
    // cross-section swept along it (gravel verges, the asphalt with painted edge lines), capped at both
    // ends, with the dashed center line on top; or, covered, the housing over it. Pieces join without a
    // seam: it reads as one road, and hovering shows a piece. Colors are in the vertices; the housing takes
    // its band of the atlas.
    ArrayMesh BuildSection(BezierSegment curve, float from, float to, Vector3 origin, bool covered = false)
    {
        float hw = BeltWidth / 2;
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
        // second point says, facing out of the road: verges, the asphalt slab, its edge lines.
        float w = hw + HousingOverhang;
        float under = -BaseDepth;
        float line0 = hw - LineInset - LineWidth, line1 = hw - LineInset;
        (float O, float H, Color C)[] profile = covered
            ? [(-w, under, HousingColor), (-w, HousingHeight, HousingColor), (w, HousingHeight, HousingRoofColor), (w, under, HousingColor), (-w, under, HousingColor)]
            : [
                (-hw - ShoulderWidth, 0.004f, ShoulderColor), (-hw, ShoulderTop, ShoulderColor), (-hw, RoadTop, RoadColor),
                (-line1, RoadTop, RoadColor), (-line0, RoadTop, RoadEdgeColor), (line0, RoadTop, RoadColor), (line1, RoadTop, RoadEdgeColor),
                (hw, RoadTop, RoadColor), (hw, ShoulderTop, RoadColor), (hw + ShoulderWidth, 0.004f, ShoulderColor),
            ];
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int e = 0; e + 1 < profile.Length; e++)
        {
            var (o0, h0, _) = profile[e];
            var (o1, h1, color) = profile[e + 1];
            bool surface = false; // nothing on a road scrolls
            var (band, repeat) = covered ? (BandHousing, HousingRepeat) : surface ? (BandTop, TopRepeat) : (BandRail, RailRepeat);
            if (_textured && covered) color = Colors.White; // the housing takes its look from the atlas; the road keeps its colors
            float v0 = band.X / 1024f, v1 = band.Y / 1024f;
            if ((o0 + o1) / 2 > 0.01f) (v0, v1) = (v1, v0); // the right side mirrors the left
            // Out of the belt: across the edge, to its left as it runs left to right (up, for the surface).
            float eo = o1 - o0, eh = (h1 == Ground ? -1 : h1) - (h0 == Ground ? -1 : h0);
            for (int i = 0; i < steps; i++)
            {
                var outward = sides[i] * -eh + Vector3.Up * eo;
                float u0 = (from + (to - from) * i / steps) / repeat, u1 = (from + (to - from) * (i + 1) / steps) / repeat;
                Quad(st, At(i, o0, h0), At(i, o1, h1), At(i + 1, o1, h1), At(i + 1, o0, h0), outward, color,
                    new(u0, v0), new(u0, v1), new(u1, v1), new(u1, v0));
            }
        }
        var back = -(centers[1] - centers[0]);
        var ahead = centers[steps] - centers[steps - 1];
        if (covered)
        {
            // Closed at both ends, dark: where the open belt goes in or comes out, it reads as a tunnel mouth.
            var mouth = new Color(0.08f, 0.08f, 0.09f);
            Quad(st, At(0, -w, under), At(0, w, under), At(0, w, HousingHeight), At(0, -w, HousingHeight), back, mouth);
            Quad(st, At(steps, -w, under), At(steps, w, under), At(steps, w, HousingHeight), At(steps, -w, HousingHeight), ahead, mouth);
            return st.Commit();
        }
        var cap = new Color(0.14f, 0.14f, 0.14f); // the slab's cut ends
        Quad(st, At(0, -hw, 0), At(0, hw, 0), At(0, hw, RoadTop), At(0, -hw, RoadTop), back, cap);
        Quad(st, At(steps, -hw, 0), At(steps, hw, 0), At(steps, hw, RoadTop), At(steps, -hw, RoadTop), ahead, cap);

        // The center line: a dash every DashPeriod m along the curve, each a straight quad just above the
        // asphalt (dashes are short, the roads gentle), cut off where this stretch ends.
        float top = RoadTop + 0.004f;
        for (float start = MathF.Floor(from / DashPeriod) * DashPeriod; start < to; start += DashPeriod)
        {
            float d0 = MathF.Max(start, from), d1 = MathF.Min(start + DashLength, to);
            if (d1 - d0 < 0.05f) continue;
            Vector3 a = ToGodot(curve.PositionAt(d0)) - origin, b = ToGodot(curve.PositionAt(d1)) - origin;
            var side = ToGodot(curve.DirectionAt((d0 + d1) / 2)).Cross(Vector3.Up).Normalized() * (DashWidth / 2);
            Quad(st, a - side + Vector3.Up * top, a + side + Vector3.Up * top, b + side + Vector3.Up * top, b - side + Vector3.Up * top, Vector3.Up, CenterLineColor);
        }
        return st.Commit();
    }

    // A box in a belt's frame at `origin`: across `o0..o1` toward `side`, `h0..h1` up, `f0..f1` along;
    // its top and four sides (nothing sees its bottom).
    static void Block(SurfaceTool st, Vector3 origin, Vector3 side, Vector3 forward, float o0, float o1, float h0, float h1,
        float f0, float f1, Color c, Vector2 a, Vector2 b, Vector2 cc, Vector2 d)
    {
        Vector3 P(float o, float h, float f) => origin + side * o + Vector3.Up * h + forward * f;
        Quad(st, P(o0, h1, f0), P(o1, h1, f0), P(o1, h1, f1), P(o0, h1, f1), Vector3.Up, c, a, b, cc, d);
        Quad(st, P(o0, h0, f0), P(o1, h0, f0), P(o1, h1, f0), P(o0, h1, f0), -forward, c, a, b, cc, d);
        Quad(st, P(o0, h0, f1), P(o1, h0, f1), P(o1, h1, f1), P(o0, h1, f1), forward, c, a, b, cc, d);
        Quad(st, P(o0, h0, f0), P(o0, h0, f1), P(o0, h1, f1), P(o0, h1, f0), -side, c, a, b, cc, d);
        Quad(st, P(o1, h0, f0), P(o1, h0, f1), P(o1, h1, f1), P(o1, h1, f0), side, c, a, b, cc, d);
    }

    // A quad a-b-c-d (in order around it) facing `outward`: wound clockwise as seen from that side, which is
    // Godot's front face, with a flat normal. Without UVs it takes a plain spot of the atlas (the middle of
    // the rail band), so a dark color stays dark.
    static readonly Vector2 PlainUv = new(0.5f, (BandRail.X + BandRail.Y) / 2048f);

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color) =>
        Quad(st, a, b, c, d, outward, color, PlainUv, PlainUv, PlainUv, PlainUv);

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color,
        Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
    {
        if ((b - a).Cross(d - a).Dot(outward) > 0) ((b, d), (ub, ud)) = ((d, b), (ud, ub));
        var normal = -(b - a).Cross(d - a);
        st.SetColor(color);
        st.SetNormal(normal.LengthSquared() > 1e-12f ? normal.Normalized() : Vector3.Up);
        st.SetUV(ua); st.AddVertex(a); st.SetUV(ub); st.AddVertex(b); st.SetUV(uc); st.AddVertex(c);
        st.SetUV(ua); st.AddVertex(a); st.SetUV(uc); st.AddVertex(c); st.SetUV(ud); st.AddVertex(d);
    }
}
