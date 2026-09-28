using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws the belt network: segments as flat ribbons colored by state, packages, spilled pickups,
/// junctions (switches get arrows to where the stream goes, and a capture ring), and gatherer posts.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between ribbon cross-sections
    const float FlashTicks = 6;     // a gatherer glows this long after a grab
    const float RingHeight = 0.24f; // capture rings sit just above the belt surface
    static readonly Vector3 PostSize = new(1.6f, 1.2f, 1.6f);

    [Export] public StandardMaterial3D BeltMaterial = null!;
    [Export] public Material? PackageMaterial;
    [Export] public Color BrokenColor = new(0.75f, 0.12f, 0.1f);
    [Export] public Color DamagedColor = new(0.55f, 0.35f, 0.1f); // a working segment near 0 health
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;
    [Export] public StandardMaterial3D JunctionMaterial = null!;
    [Export] public Material? IndicatorMaterial;
    [Export] public StandardMaterial3D GathererMaterial = null!;

    sealed record JunctionView(StandardMaterial3D Disc, MeshInstance3D[] Arrows, CaptureRing? Ring);
    sealed record PostView(Node3D Root, StandardMaterial3D Material, HealthBar Health);

    // One material per segment, indexed [line][segment], so each can show its own state.
    readonly List<StandardMaterial3D[]> _segmentMaterials = [];
    readonly List<JunctionView> _junctions = [];
    readonly Dictionary<int, PostView> _posts = []; // by gatherer id; destroyed posts go away
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    InstanceBatch _packages = null!, _pickups = null!;

    public void Build(SimState state)
    {
        foreach (var line in state.Belts)
        {
            var materials = new StandardMaterial3D[line.Segments.Length];
            for (int s = 0; s < line.Segments.Length; s++)
            {
                materials[s] = (StandardMaterial3D)BeltMaterial.Duplicate();
                AddChild(new MeshInstance3D { Mesh = BuildRibbon(line.Segments[s].Curve), MaterialOverride = materials[s] });
            }
            _segmentMaterials.Add(materials);
        }

        foreach (var junction in state.Junctions) _junctions.Add(BuildJunction(state, junction));

        var box = new BoxMesh { Size = Vector3.One * PackageSize, Material = PackageMaterial };
        _packages = new InstanceBatch(this, box);
        _pickups = new InstanceBatch(this, box);
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
            SyncSegmentColors(line, _segmentMaterials[l]);
            foreach (var p in line.Packages)
                _packages.Add(ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) + lift, ToGodot(p.Direction));
        }
        _packages.End();

        SyncJunctions(state);
        SyncPosts(state);

        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            float yaw = p.Id * 2.4f; // golden-angle steps: a stable, scattered look per pickup
            _pickups.Add(ToGodot(p.Position) + lift, new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)));
        }
        _pickups.End();
    }

    JunctionView BuildJunction(SimState state, Junction junction)
    {
        var disc = (StandardMaterial3D)JunctionMaterial.Duplicate();
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 0.9f, Height = 0.3f },
            MaterialOverride = disc,
            Position = ToGodot(junction.Position),
        });
        if (!junction.IsSwitch) return new JunctionView(disc, [], null); // a merge has nothing to show

        // An arrow just past the disc along each output, and a ring at the capture radius.
        var mesh = new BoxMesh { Size = new Vector3(0.35f, 0.15f, 1f), Material = IndicatorMaterial };
        var arrows = new MeshInstance3D[junction.Outputs.Count];
        for (int o = 0; o < arrows.Length; o++)
        {
            var direction = (ToGodot(state.Belts[junction.Outputs[o]].DirectionAt(1f)) with { Y = 0 }).Normalized();
            var at = ToGodot(junction.Position) + direction * 1.5f + new Vector3(0, 0.25f, 0);
            AddChild(arrows[o] = new MeshInstance3D { Mesh = mesh, Transform = new Transform3D(Basis.LookingAt(direction, Vector3.Up), at) });
        }
        var ring = new CaptureRing { Radius = Simulation.CaptureRadius, Position = ToGodot(junction.Position) with { Y = RingHeight } };
        AddChild(ring);
        return new JunctionView(disc, arrows, ring);
    }

    // Discs take the owner's color. A switch shows an arrow on every output while it splits the stream
    // (neutral), then only on the one it feeds; its ring fills with the capturer's color.
    void SyncJunctions(SimState state)
    {
        for (int j = 0; j < state.Junctions.Count; j++)
        {
            var junction = state.Junctions[j];
            var view = _junctions[j];
            var owned = junction.Owner == Player.None
                ? JunctionMaterial.AlbedoColor
                : JunctionMaterial.AlbedoColor.Lerp(PlayerPalette.Color(junction.Owner), 0.75f);
            if (view.Disc.AlbedoColor != owned) view.Disc.AlbedoColor = owned;
            if (!junction.IsSwitch) continue;

            for (int o = 0; o < view.Arrows.Length; o++)
            {
                bool feeds = junction.Selected < 0 || junction.Selected == o;
                if (view.Arrows[o].Visible != feeds) view.Arrows[o].Visible = feeds;
            }

            var zone = PlayerPalette.Color(junction.Owner) with { A = 0.45f };
            var progress = PlayerPalette.Color(junction.Capturer) with { A = 0.95f };
            view.Ring!.Set(junction.CaptureProgress, zone, progress);
        }
    }

    void SyncPosts(SimState state)
    {
        _seen.Clear();
        foreach (var gatherer in state.Gatherers)
        {
            _seen.Add(gatherer.Id);
            if (!_posts.TryGetValue(gatherer.Id, out var post)) _posts[gatherer.Id] = post = BuildPost(state, gatherer);

            float energy = state.Tick - gatherer.LastGrabTick < FlashTicks ? 1.5f : 0f;
            if (post.Material.EmissionEnergyMultiplier != energy) post.Material.EmissionEnergyMultiplier = energy;
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

    // Working segments shift toward DamagedColor as health drops. Broken ones are red,
    // fading back toward normal as repair restores health.
    void SyncSegmentColors(BeltLine line, StandardMaterial3D[] materials)
    {
        var normal = BeltMaterial.AlbedoColor;
        for (int s = 0; s < line.Segments.Length; s++)
        {
            var segment = line.Segments[s];
            float health = segment.Health / segment.MaxHealth;
            var color = segment.State == SegmentState.Broken
                ? BrokenColor.Lerp(normal, health)
                : normal.Lerp(DamagedColor, 1 - health);
            if (materials[s].AlbedoColor != color) materials[s].AlbedoColor = color;
        }
    }

    ArrayMesh BuildRibbon(BezierSegment curve)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(curve.Length / RibbonStep));
        var left = new Vector3[steps + 1];
        var right = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float d = curve.Length * i / steps;
            var center = ToGodot(curve.PositionAt(d));
            var side = ToGodot(curve.DirectionAt(d)).Cross(Vector3.Up).Normalized() * (BeltWidth / 2);
            left[i] = center - side;
            right[i] = center + side;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetNormal(Vector3.Up);
        // Godot's front faces wind clockwise as seen by the camera (here: from above).
        for (int i = 0; i < steps; i++)
        {
            st.AddVertex(left[i]); st.AddVertex(right[i + 1]); st.AddVertex(right[i]);
            st.AddVertex(left[i]); st.AddVertex(left[i + 1]); st.AddVertex(right[i + 1]);
        }
        return st.Commit();
    }
}
