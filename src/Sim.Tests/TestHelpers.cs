using System.Numerics;

namespace Sim.Tests;

static class TestHelpers
{
    public const int Blue = 0, Red = 1;
    public const int T = Simulation.TicksPerSecond;
    public static readonly Command[] NoCommands = [];

    /// <summary>A simulation with two players, Blue and Red.</summary>
    public static Simulation NewSim()
    {
        var sim = new Simulation();
        sim.AddPlayer();
        sim.AddPlayer();
        return sim;
    }

    // Evenly spaced control points on a line: t advances uniformly with distance.
    public static BezierSegment Straight(Vector3 from, Vector3 to) =>
        new(from, Vector3.Lerp(from, to, 1 / 3f), Vector3.Lerp(from, to, 2 / 3f), to);

    // 0-10 m and 10-20 m along +X.
    public static BezierSegment[] TwoSegments() =>
        [Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(20, 0, 0))];

    // 2 m/s, 1 m spacing, 100 health per segment.
    public static BeltConfig Belt(float spawnIntervalSeconds, float spillLoss = 0) =>
        new(Speed: 2, Spacing: 1, SpawnIntervalSeconds: spawnIntervalSeconds, SpillLoss: spillLoss);

    /// <summary>A splash gun that fires every tick, `dps` in all: only splash breaks road, so road tests use it.</summary>
    public static WeaponType Blast(float dps, float range = 8) =>
        new(WeaponKind.Bullet, dps * Simulation.Dt, Simulation.Dt, range, Hit: HitKind.Splash, SplashRadius: 0.5f);

    public static void Run(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(NoCommands);
    }

    /// <summary>Ticks until `event` happens (the first tick counts as 1); -1 if it doesn't within `maxTicks`.</summary>
    public static int TicksUntil(Simulation sim, SimEvent expected, int maxTicks, IReadOnlyList<Command>? first = null)
    {
        var events = sim.Tick(first ?? NoCommands);
        for (int tick = 1; tick <= maxTicks; tick++)
        {
            if (events.Contains(expected)) return tick;
            events = sim.Tick(NoCommands);
        }
        return -1;
    }

    public static Unit UnitById(Simulation sim, int id) => sim.State.Units.Single(u => u.Id == id);
}
