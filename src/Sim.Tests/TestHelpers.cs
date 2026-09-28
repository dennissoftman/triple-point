using System.Numerics;

namespace Sim.Tests;

static class TestHelpers
{
    public static readonly Command[] NoCommands = [];

    // Evenly spaced control points on a line: t advances uniformly with distance.
    public static BezierSegment Straight(Vector3 from, Vector3 to) =>
        new(from, Vector3.Lerp(from, to, 1 / 3f), Vector3.Lerp(from, to, 2 / 3f), to);

    // 0-10 m and 10-20 m along +X.
    public static BezierSegment[] TwoSegments() =>
        [Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(20, 0, 0))];

    // 2 m/s, 1 m spacing, 100 health per segment.
    public static BeltConfig Belt(float spawnIntervalSeconds, float spillLoss = 0) =>
        new(Speed: 2, Spacing: 1, SpawnIntervalSeconds: spawnIntervalSeconds, SpillLoss: spillLoss);

    public static void Run(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(NoCommands);
    }
}
