namespace Sim;

/// <summary>
/// Sin, Cos and Atan2 for the simulation, from +, -, *, / and rounding alone: those are exact IEEE
/// operations, the same on every x64 machine, while MathF's call the C runtime, whose results may differ
/// between Windows versions (and between Windows, Linux and macOS). Lockstep needs every machine to compute
/// the same bits. Worked in double and rounded to float once; within about 1e-7 of MathF. The sim uses
/// these, never MathF.Sin, Cos or Atan2 (DeterminismTests checks the source). Sqrt, Floor, Ceiling, Round,
/// Abs, Min and Max are exact, so MathF's are fine.
///
/// Lerp too: System.Numerics' Lerp (Vector2/3/4, float) and Vector3.Reflect use a fused multiply-add on CPUs
/// with AVX2 and separate ones without (one rounding instead of two), so the same call gives different
/// last bits on different machines. Found by the cross-JIT check (DeterminismTests): the road network
/// differed at load. Vector arithmetic, Dot, Length, Distance, Normalize, Cross, Min, Max and Transform
/// were the same bit for bit everywhere.
/// </summary>
public static class SimMath
{
    /// <summary>a + (b - a) t, by separate operations (never fused), the same on every CPU.</summary>
    public static System.Numerics.Vector3 Lerp(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float t) => a + (b - a) * t;

    const double HalfPi = 1.5707963267948966, Pi = 3.141592653589793;
    const double TwoPiHi = 6.283185307179586, TwoPiLo = 2.4492935982947064e-16; // 2π split, for range reduction

    public static float Sin(float x) => (float)SinD(x);

    public static float Cos(float x) => (float)SinD(x + HalfPi);

    public static (float Sin, float Cos) SinCos(float x) => (Sin(x), Cos(x));

    // x reduced to [-π, π], then folded onto [-π/2, π/2] (sin(π - r) = sin r) for the series.
    static double SinD(double x)
    {
        if (!double.IsFinite(x)) return double.NaN;
        double k = Math.Round(x / TwoPiHi);
        double r = x - k * TwoPiHi - k * TwoPiLo;
        if (r > HalfPi) r = Pi - r;
        else if (r < -HalfPi) r = -Pi - r;
        // Taylor to r^17: below 1e-12 on [-π/2, π/2], far under float's resolution.
        double r2 = r * r;
        double p = 1.0 / 355687428096000;                // 1/17!
        p = p * r2 - 1.0 / 1307674368000;                // 1/15!
        p = p * r2 + 1.0 / 6227020800;                   // 1/13!
        p = p * r2 - 1.0 / 39916800;                     // 1/11!
        p = p * r2 + 1.0 / 362880;                       // 1/9!
        p = p * r2 - 1.0 / 5040;                         // 1/7!
        p = p * r2 + 1.0 / 120;                          // 1/5!
        p = p * r2 - 1.0 / 6;                            // 1/3!
        return r + r * r2 * p;
    }

    /// <summary>The angle of (x, y) from +x, in [-π, π], with MathF.Atan2's signs and special cases for finite inputs.</summary>
    public static float Atan2(float y, float x)
    {
        if (float.IsNaN(x) || float.IsNaN(y)) return float.NaN;
        double ay = Math.Abs((double)y), ax = Math.Abs((double)x);
        double angle;
        if (ax == 0 && ay == 0) angle = 0;
        else if (ay <= ax) angle = Atan(ay / ax);
        else angle = HalfPi - Atan(ax / ay);
        if (double.IsNegative(x)) angle = Pi - angle; // x < 0, and x = -0 like MathF (atan2(0, -0) = π)
        return (float)(double.IsNegative(y) ? -angle : angle);
    }

    // atan on [0, 1]: halved twice (atan t = 2 atan(t / (1 + sqrt(1 + t²)))) to below tan(π/16), then a series.
    static double Atan(double t)
    {
        t /= 1 + Math.Sqrt(1 + t * t);
        t /= 1 + Math.Sqrt(1 + t * t);
        // |t| <= 0.199: the series to t^21 is below 1e-16.
        double t2 = t * t, p = 0;
        for (int n = 21; n >= 3; n -= 2) p = (((n >> 1) & 1) == 1 ? -1.0 : 1.0) / n + t2 * p;
        return 4 * (t + t * t2 * p);
    }
}
