using Godot;

/// <summary>
/// Many copies of one mesh through a MultiMesh. Transforms are written to a C# array and sent to
/// the engine as one buffer per frame, instead of one engine call per instance.
/// Usage per frame: Begin(count), Add(...) for each instance, End().
/// </summary>
public sealed class InstanceBatch
{
    const int FloatsPerInstance = 12; // Transform3D as a 3x4 row-major matrix

    readonly MultiMesh _multimesh;
    float[] _buffer = [];
    int _count;

    public InstanceBatch(Node parent, Mesh mesh)
    {
        _multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
        Grow(64);
        parent.AddChild(new MultiMeshInstance3D { Multimesh = _multimesh });
    }

    public void Begin(int count)
    {
        if (count > _multimesh.InstanceCount) Grow(count * 2);
        _count = 0;
    }

    /// <summary>An upright instance at `origin` facing `forward` (+Z of the mesh), `scale` times its size.</summary>
    public void Add(Vector3 origin, Vector3 forward, float scale = 1)
    {
        var z = forward;
        var x = Vector3.Up.Cross(z).Normalized();
        var y = z.Cross(x);
        (x, y, z) = (x * scale, y * scale, z * scale);

        int o = _count++ * FloatsPerInstance;
        _buffer[o + 0] = x.X; _buffer[o + 1] = y.X; _buffer[o + 2] = z.X; _buffer[o + 3] = origin.X;
        _buffer[o + 4] = x.Y; _buffer[o + 5] = y.Y; _buffer[o + 6] = z.Y; _buffer[o + 7] = origin.Y;
        _buffer[o + 8] = x.Z; _buffer[o + 9] = y.Z; _buffer[o + 10] = z.Z; _buffer[o + 11] = origin.Z;
    }

    public void End()
    {
        _multimesh.Buffer = _buffer;
        _multimesh.VisibleInstanceCount = _count;
    }

    void Grow(int count)
    {
        _multimesh.InstanceCount = count; // resets the engine-side buffer; End() refills it
        _buffer = new float[count * FloatsPerInstance];
    }
}
