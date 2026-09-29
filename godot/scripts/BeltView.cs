using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws the belts: segments as flat ribbons colored by state, packages, spilled pickups, and gatherer posts.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between ribbon cross-sections
    const float FlashTicks = 6;     // a gatherer glows this long after a grab
    static readonly Vector3 PostSize = new(1.6f, 1.2f, 1.6f);

    [Export] public StandardMaterial3D BeltMaterial = null!;
    [Export] public Material? PackageMaterial;
    [Export] public Color BrokenColor = new(0.75f, 0.12f, 0.1f);
    [Export] public Color DamagedColor = new(0.55f, 0.35f, 0.1f); // a working segment near 0 health
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;
    [Export] public StandardMaterial3D GathererMaterial = null!;

    // What each view last showed is kept here in C#, so a frame only calls into the engine for what changed.
    sealed record PostView(Node3D Root, StandardMaterial3D Material, HealthBar Health)
    {
        public bool Glowing;
    }

    // One material per segment, indexed [line][segment], so each can show its own state, and the color
    // each shows. (Dev-grade: a draw call per segment. See the rendering debt in docs/architecture.md.)
    readonly List<StandardMaterial3D[]> _segmentMaterials = [];
    readonly List<Color[]> _segmentColors = [];
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
            _segmentColors.Add(Enumerable.Repeat(BeltMaterial.AlbedoColor, materials.Length).ToArray());
        }

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
            SyncSegmentColors(line, _segmentMaterials[l], _segmentColors[l]);
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

    // Working segments shift toward DamagedColor as health drops. Broken ones are red,
    // fading back toward normal as repair restores health.
    void SyncSegmentColors(BeltLine line, StandardMaterial3D[] materials, Color[] shown)
    {
        var normal = BeltMaterial.AlbedoColor;
        for (int s = 0; s < line.Segments.Length; s++)
        {
            var segment = line.Segments[s];
            float health = segment.Health / segment.MaxHealth;
            var color = segment.State == SegmentState.Broken
                ? BrokenColor.Lerp(normal, health)
                : normal.Lerp(DamagedColor, 1 - health);
            if (shown[s] != color) materials[s].AlbedoColor = shown[s] = color;
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
