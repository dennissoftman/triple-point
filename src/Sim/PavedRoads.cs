using System.Numerics;

namespace Sim;

/// <summary>
/// Where units move faster: a grid of 1 m cells marking the ground within a road's half width of an open,
/// paved piece that isn't broken. Rebuilt only when a piece is paved, breaks or is repaired, so looking a
/// unit up is a cell read. It covers the routes' bounds and grows (allocating) only when a route is added.
/// </summary>
sealed class PavedRoads
{
    const float Cell = 1f;
    const float SampleStep = 0.5f; // m along a piece between the points that mark cells

    float _minX, _minZ;
    int _width, _depth;
    bool[] _paved = [];

    public bool At(Vector3 p)
    {
        int x = (int)MathF.Floor((p.X - _minX) / Cell), z = (int)MathF.Floor((p.Z - _minZ) / Cell);
        return x >= 0 && z >= 0 && x < _width && z < _depth && _paved[z * _width + x];
    }

    public void Rebuild(List<BeltLine> belts, float halfWidth)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var line in belts)
            foreach (var s in line.Segments)
                for (float d = 0; d <= s.Curve.Length; d += SampleStep)
                {
                    var p = s.Curve.PositionAt(d);
                    (minX, minZ, maxX, maxZ) = (MathF.Min(minX, p.X), MathF.Min(minZ, p.Z), MathF.Max(maxX, p.X), MathF.Max(maxZ, p.Z));
                }
        if (minX > maxX) return; // no roads
        (_minX, _minZ) = (minX - halfWidth - Cell, minZ - halfWidth - Cell);
        int width = (int)MathF.Ceiling((maxX + halfWidth + Cell - _minX) / Cell), depth = (int)MathF.Ceiling((maxZ + halfWidth + Cell - _minZ) / Cell);
        if (width * depth > _paved.Length) _paved = new bool[width * depth];
        else Array.Clear(_paved);
        (_width, _depth) = (width, depth);

        int reach = (int)MathF.Ceiling(halfWidth / Cell);
        foreach (var line in belts)
            foreach (var s in line.Segments)
            {
                if (!s.Paved || s.Covered || s.State == SegmentState.Broken) continue;
                for (float d = 0; d <= s.Curve.Length; d += SampleStep)
                {
                    var p = s.Curve.PositionAt(d);
                    int cx = (int)MathF.Floor((p.X - _minX) / Cell), cz = (int)MathF.Floor((p.Z - _minZ) / Cell);
                    for (int z = cz - reach; z <= cz + reach; z++)
                        for (int x = cx - reach; x <= cx + reach; x++)
                        {
                            if (x < 0 || z < 0 || x >= _width || z >= _depth) continue;
                            float dx = _minX + (x + 0.5f) * Cell - p.X, dz = _minZ + (z + 0.5f) * Cell - p.Z;
                            if (dx * dx + dz * dz <= halfWidth * halfWidth) _paved[z * _width + x] = true;
                        }
                }
            }
    }
}
