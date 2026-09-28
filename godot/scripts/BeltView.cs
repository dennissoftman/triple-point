using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws belt segments as flat ribbons colored by state, and all packages through one MultiMesh.
/// Package transforms go to the engine as a single buffer per frame, not one call per package.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between ribbon cross-sections
    const int FloatsPerInstance = 12; // Transform3D as a 3x4 row-major matrix

    [Export] public StandardMaterial3D BeltMaterial = null!;
    [Export] public Material? PackageMaterial;
    [Export] public Color BrokenColor = new(0.75f, 0.12f, 0.1f);
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;

    // One material per segment, indexed [line][segment], so each can show its own state.
    readonly List<StandardMaterial3D[]> _segmentMaterials = [];
    MultiMesh _packages = null!;
    float[] _buffer = [];

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

        _packages = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh { Size = Vector3.One * PackageSize, Material = PackageMaterial },
        };
        Grow(64);
        AddChild(new MultiMeshInstance3D { Multimesh = _packages });
    }

    public void Sync(SimState state, float alpha)
    {
        int total = 0;
        foreach (var line in state.Belts) total += line.Packages.Count;
        if (total > _packages.InstanceCount) Grow(total * 2);

        var lift = new Vector3(0, PackageSize / 2, 0); // sit on the belt, not in it
        int n = 0;
        for (int l = 0; l < state.Belts.Count; l++)
        {
            var line = state.Belts[l];
            SyncSegmentColors(line, _segmentMaterials[l]);
            foreach (var p in line.Packages)
            {
                var origin = ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) + lift;
                var z = ToGodot(p.Direction);
                var x = Vector3.Up.Cross(z).Normalized();
                var y = z.Cross(x);

                int o = n++ * FloatsPerInstance;
                _buffer[o + 0] = x.X; _buffer[o + 1] = y.X; _buffer[o + 2] = z.X; _buffer[o + 3] = origin.X;
                _buffer[o + 4] = x.Y; _buffer[o + 5] = y.Y; _buffer[o + 6] = z.Y; _buffer[o + 7] = origin.Y;
                _buffer[o + 8] = x.Z; _buffer[o + 9] = y.Z; _buffer[o + 10] = z.Z; _buffer[o + 11] = origin.Z;
            }
        }
        _packages.Buffer = _buffer;
        _packages.VisibleInstanceCount = total;
    }

    // Broken is red, fading back toward normal as repair progresses.
    void SyncSegmentColors(BeltLine line, StandardMaterial3D[] materials)
    {
        for (int s = 0; s < line.Segments.Length; s++)
        {
            var segment = line.Segments[s];
            var color = segment.State == SegmentState.Broken
                ? BrokenColor.Lerp(BeltMaterial.AlbedoColor, segment.RepairProgress)
                : BeltMaterial.AlbedoColor;
            if (materials[s].AlbedoColor != color) materials[s].AlbedoColor = color;
        }
    }

    void Grow(int count)
    {
        _packages.InstanceCount = count; // resets the engine-side buffer; the next Sync refills it
        _buffer = new float[count * FloatsPerInstance];
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
