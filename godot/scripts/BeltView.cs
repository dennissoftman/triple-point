using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>Draws belt segments as flat ribbons colored by state, packages on the belt, and spilled pickups.</summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between ribbon cross-sections

    [Export] public StandardMaterial3D BeltMaterial = null!;
    [Export] public Material? PackageMaterial;
    [Export] public Color BrokenColor = new(0.75f, 0.12f, 0.1f);
    [Export] public Color DamagedColor = new(0.55f, 0.35f, 0.1f); // a working segment near 0 health
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;

    // One material per segment, indexed [line][segment], so each can show its own state.
    readonly List<StandardMaterial3D[]> _segmentMaterials = [];
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

        _pickups.Begin(state.Pickups.Count);
        foreach (var p in state.Pickups)
        {
            float yaw = p.Id * 2.4f; // golden-angle steps: a stable, scattered look per pickup
            _pickups.Add(ToGodot(p.Position) + lift, new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)));
        }
        _pickups.End();
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
