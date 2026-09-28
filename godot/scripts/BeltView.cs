using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws the belt network: segments as flat ribbons colored by state, packages, spilled pickups,
/// junctions (with an arrow at switches pointing to the live output), and gatherer posts.
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
    [Export] public Material? JunctionMaterial;
    [Export] public Material? IndicatorMaterial;
    [Export] public StandardMaterial3D GathererMaterial = null!;

    // One material per segment, indexed [line][segment], so each can show its own state.
    readonly List<StandardMaterial3D[]> _segmentMaterials = [];
    readonly List<MeshInstance3D> _switchArrows = [];         // per junction
    readonly List<StandardMaterial3D> _gathererMaterials = []; // per gatherer, for the grab flash
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

        foreach (var junction in state.Junctions)
        {
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 0.9f, Height = 0.3f, Material = JunctionMaterial },
                Position = ToGodot(junction.Position),
            });
            var arrow = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.15f, 1f), Material = IndicatorMaterial },
                Visible = junction.Outputs.Count > 1, // only switches have a choice to show
            };
            AddChild(arrow);
            _switchArrows.Add(arrow);
        }

        foreach (var gatherer in state.Gatherers)
        {
            var material = (StandardMaterial3D)GathererMaterial.Duplicate();
            _gathererMaterials.Add(material);
            var post = ToGodot(gatherer.Position);
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = PostSize },
                MaterialOverride = material,
                Position = post + new Vector3(0, PostSize.Y / 2, 0),
            });
            // An arm from the post to its pull point on the belt.
            var pull = ToGodot(state.Belts[gatherer.Line].PositionAt(gatherer.Distance));
            var from = post with { Y = pull.Y + 0.1f };
            var reach = pull - from;
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.12f, reach.Length()) },
                MaterialOverride = material,
                Transform = new Transform3D(Basis.LookingAt(reach, Vector3.Up), from + reach / 2),
            });
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
            SyncSegmentColors(line, _segmentMaterials[l]);
            foreach (var p in line.Packages)
                _packages.Add(ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) + lift, ToGodot(p.Direction));
        }
        _packages.End();

        SyncJunctions(state);
        SyncGatherers(state);

        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            float yaw = p.Id * 2.4f; // golden-angle steps: a stable, scattered look per pickup
            _pickups.Add(ToGodot(p.Position) + lift, new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)));
        }
        _pickups.End();
    }

    // A switch's arrow sits just past the junction, along the live output.
    void SyncJunctions(SimState state)
    {
        for (int j = 0; j < state.Junctions.Count; j++)
        {
            var junction = state.Junctions[j];
            if (junction.Outputs.Count < 2) continue;
            var output = state.Belts[junction.Outputs[junction.Selected]];
            var direction = (ToGodot(output.DirectionAt(1f)) with { Y = 0 }).Normalized();
            var at = ToGodot(junction.Position) + direction * 1.5f + new Vector3(0, 0.25f, 0);
            _switchArrows[j].Transform = new Transform3D(Basis.LookingAt(direction, Vector3.Up), at);
        }
    }

    void SyncGatherers(SimState state)
    {
        for (int g = 0; g < state.Gatherers.Count; g++)
        {
            float energy = state.Tick - state.Gatherers[g].LastGrabTick < FlashTicks ? 1.5f : 0f;
            if (_gathererMaterials[g].EmissionEnergyMultiplier != energy) _gathererMaterials[g].EmissionEnergyMultiplier = energy;
        }
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
