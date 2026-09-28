using System.Numerics;

namespace Sim;

/// <summary>
/// Cubic Bezier curve with an arc-length table, so things can move along it at constant speed.
/// The curve parameter t does not advance uniformly with distance, so all public queries take
/// a distance in meters from the segment start instead of t.
/// </summary>
public sealed class BezierSegment
{
    const int Samples = 32;

    public readonly Vector3 P0, P1, P2, P3;
    public readonly float Length;

    // Arc length from the start to sample i (t = i / Samples).
    readonly float[] _cumulative = new float[Samples + 1];

    public BezierSegment(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        (P0, P1, P2, P3) = (p0, p1, p2, p3);
        var prev = p0;
        for (int i = 1; i <= Samples; i++)
        {
            var p = Evaluate(i / (float)Samples);
            _cumulative[i] = _cumulative[i - 1] + Vector3.Distance(prev, p);
            prev = p;
        }
        Length = _cumulative[Samples];
    }

    public Vector3 PositionAt(float distance) => Evaluate(ParamAt(distance));

    public Vector3 DirectionAt(float distance)
    {
        var d = Derivative(ParamAt(distance));
        // A zero-length handle makes the derivative vanish at that end.
        if (d.LengthSquared() < 1e-8f) d = P3 - P0;
        return Vector3.Normalize(d);
    }

    Vector3 Evaluate(float t)
    {
        float u = 1 - t;
        return u * u * u * P0 + 3 * u * u * t * P1 + 3 * u * t * t * P2 + t * t * t * P3;
    }

    Vector3 Derivative(float t)
    {
        float u = 1 - t;
        return 3 * u * u * (P1 - P0) + 6 * u * t * (P2 - P1) + 3 * t * t * (P3 - P2);
    }

    float ParamAt(float distance)
    {
        if (distance <= 0) return 0;
        if (distance >= Length) return 1;
        int i = Array.BinarySearch(_cumulative, distance);
        if (i >= 0) return i / (float)Samples;
        i = ~i; // first sample past `distance`, in [1, Samples]
        float a = _cumulative[i - 1], b = _cumulative[i];
        return (i - 1 + (distance - a) / (b - a)) / Samples;
    }
}
