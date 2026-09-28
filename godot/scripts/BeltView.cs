using System;
using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws belt lines as flat ribbons and all packages through one MultiMesh.
/// Package transforms go to the engine as a single buffer per frame, not one call per package.
/// </summary>
public partial class BeltView : Node3D
{
    const float RibbonStep = 0.25f; // meters between ribbon cross-sections
    const int FloatsPerInstance = 12; // Transform3D as a 3x4 row-major matrix

    [Export] public Material? BeltMaterial;
    [Export] public Material? PackageMaterial;
    [Export] public float BeltWidth = 1.2f;
    [Export] public float PackageSize = 0.6f;

    MultiMesh _packages = null!;
    float[] _buffer = [];

    public void Build(SimState state)
    {
        foreach (var line in state.Belts)
            AddChild(new MeshInstance3D { Mesh = BuildRibbon(line), MaterialOverride = BeltMaterial });

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
        var packages = state.Packages;
        if (packages.Count > _packages.InstanceCount) Grow(packages.Count * 2);

        var lift = new Vector3(0, PackageSize / 2, 0); // sit on the belt, not in it
        for (int i = 0; i < packages.Count; i++)
        {
            var p = packages[i];
            var origin = ToGodot(p.PrevPosition).Lerp(ToGodot(p.Position), alpha) + lift;
            var z = ToGodot(p.Direction);
            var x = Vector3.Up.Cross(z).Normalized();
            var y = z.Cross(x);

            int o = i * FloatsPerInstance;
            _buffer[o + 0] = x.X; _buffer[o + 1] = y.X; _buffer[o + 2] = z.X; _buffer[o + 3] = origin.X;
            _buffer[o + 4] = x.Y; _buffer[o + 5] = y.Y; _buffer[o + 6] = z.Y; _buffer[o + 7] = origin.Y;
            _buffer[o + 8] = x.Z; _buffer[o + 9] = y.Z; _buffer[o + 10] = z.Z; _buffer[o + 11] = origin.Z;
        }
        _packages.Buffer = _buffer;
        _packages.VisibleInstanceCount = packages.Count;
    }

    void Grow(int count)
    {
        _packages.InstanceCount = count; // resets the engine-side buffer; the next Sync refills it
        _buffer = new float[count * FloatsPerInstance];
    }

    ArrayMesh BuildRibbon(BeltLine line)
    {
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (var segment in line.Segments)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling(segment.Length / RibbonStep));
            for (int i = 0; i <= steps; i++)
            {
                float d = segment.Length * i / steps;
                var center = ToGodot(segment.PositionAt(d));
                var side = ToGodot(segment.DirectionAt(d)).Cross(Vector3.Up).Normalized() * (BeltWidth / 2);
                left.Add(center - side);
                right.Add(center + side);
            }
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetNormal(Vector3.Up);
        // Godot's front faces wind clockwise as seen by the camera (here: from above).
        for (int i = 0; i + 1 < left.Count; i++)
        {
            st.AddVertex(left[i]); st.AddVertex(right[i + 1]); st.AddVertex(right[i]);
            st.AddVertex(left[i]); st.AddVertex(left[i + 1]); st.AddVertex(right[i + 1]);
        }
        return st.Commit();
    }
}
