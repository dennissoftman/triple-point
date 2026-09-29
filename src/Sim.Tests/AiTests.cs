using Sim.Ai;
using Xunit.Abstractions;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>The commander AI, on the real main map.</summary>
public class AiTests(ITestOutputHelper output)
{
    // Plays up to `seconds` of game time with these commanders, stopping when the game ends. Calls
    // `watch` with each tick's commands and events.
    static void Play(Simulation sim, Commander[] ais, int seconds,
        Action<IReadOnlyList<Command>, IReadOnlyList<SimEvent>>? watch = null)
    {
        var commands = new List<Command>();
        for (int t = 0; t < seconds * T && !sim.State.GameOver; t++)
        {
            commands.Clear();
            foreach (var ai in ais) commands.AddRange(ai.Think());
            var events = sim.Tick(commands);
            watch?.Invoke(commands, events);
        }
    }

    static int Minutes(Simulation sim) => sim.State.Tick / T / 60;

    static string Summary(Simulation sim, int player)
    {
        var s = sim.State;
        int posts = s.Gatherers.Count(g => g.Owner == player);
        var buildings = string.Join(",", s.Buildings.Where(b => b.Owner == player).Select(b => b.Type.Id + (b.Built ? "" : "*")));
        var units = string.Join(",", s.Units.Where(u => u.Owner == player).GroupBy(u => u.Type).Select(g => $"{g.Key}x{g.Count()}"));
        return $"p{player}: {s.Players[player].Packages} pkg, {posts} posts, [{buildings}], [{units}]";
    }

    [Fact]
    public void Takes_posts_and_raises_an_army_in_the_first_minutes()
    {
        var sim = MainMap.Load();
        Play(sim, [new Commander(sim, Red)], 4 * 60);
        output.WriteLine(Summary(sim, Red));

        var s = sim.State;
        Assert.True(s.Gatherers.Count(g => g.Owner == Red) >= 2, "at least two posts");
        Assert.Contains(s.Buildings, b => b.Owner == Red && b.Built && b.Type.Id == "barracks");
        Assert.True(s.Units.Count(u => u.Owner == Red && u.Damage > 0) >= 4, "an army");
        // its posts are on its own half
        var home = s.Buildings.First(b => b.Owner == Red && b.Type.Id == "hq").Position;
        var theirs = s.Buildings.First(b => b.Owner == Blue && b.Type.Id == "hq").Position;
        Assert.All(s.Gatherers.Where(g => g.Owner == Red),
            g => Assert.True(System.Numerics.Vector3.Distance(g.Position, home) < System.Numerics.Vector3.Distance(g.Position, theirs)));
    }

    [Fact]
    public void Beats_an_opponent_that_stops_playing_by_going_for_the_belt()
    {
        // Blue plays its opening, then stops: it has posts and units to beat, and belt to cut.
        var sim = MainMap.Load();
        var blue = new Commander(sim, Blue);
        var red = new Commander(sim, Red);
        Play(sim, [blue, red], 150);
        Assert.True(sim.State.Gatherers.Count(g => g.Owner == Blue) >= 2);

        int brokenBlueBelt = 0, blueLosses = 0;
        var bluePosts = sim.State.Gatherers.Where(g => g.Owner == Blue).Select(g => (g.Line, g.Distance)).ToList();
        Play(sim, [red], 12 * 60, (_, events) =>
        {
            foreach (var e in events)
            {
                if (e.Kind == SimEventKind.SegmentBroken && bluePosts.Any(p => p.Line == e.Id)) brokenBlueBelt++;
                if (e.Kind == SimEventKind.GathererDestroyed) blueLosses++;
            }
        });
        output.WriteLine($"{Minutes(sim)} min: broke Blue's belt {brokenBlueBelt}x, {blueLosses} posts destroyed");
        Assert.True(sim.State.GameOver, Summary(sim, Blue) + " / " + Summary(sim, Red));
        Assert.Equal(Red, sim.State.Winner);
        Assert.True(brokenBlueBelt + blueLosses > 0, "it went for the economy");
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Two_commanders_finish_a_match(uint seed)
    {
        var sim = MainMap.Load(seed);
        int broken = 0, collected = 0;
        Play(sim, [new Commander(sim, Blue), new Commander(sim, Red)], 30 * 60, (_, events) =>
        {
            foreach (var e in events)
            {
                if (e.Kind == SimEventKind.SegmentBroken) broken++;
                if (e.Kind == SimEventKind.PickupCollected) collected++;
            }
        });
        output.WriteLine($"seed {seed}: {sim.State.Tick / T} s, winner {sim.State.Winner}, {broken} segments broken, {collected} spilled packages picked up");
        Assert.True(sim.State.GameOver, Summary(sim, Blue) + " / " + Summary(sim, Red));
        Assert.NotEqual(Player.None, sim.State.Winner);
        Assert.True(broken > 0, "belts get fought over");
    }

    [Fact]
    public void Commands_only_its_own_units_and_buildings()
    {
        var sim = MainMap.Load();
        var ais = new[] { new Commander(sim, Blue), new Commander(sim, Red) };
        var commands = new List<Command>();
        int checkedCount = 0;
        for (int t = 0; t < 12 * 60 * T && !sim.State.GameOver; t++)
        {
            commands.Clear();
            foreach (var ai in ais)
                foreach (var c in ai.Think())
                {
                    Assert.Equal(ai.Player, c.Player);
                    int? unit = c switch
                    {
                        MoveCommand m => m.UnitId, AttackCommand m => m.UnitId, AttackMoveCommand m => m.UnitId,
                        AttackSegmentCommand m => m.UnitId, RepairSegmentCommand m => m.UnitId,
                        BuildCommand m => m.UnitId, ResumeBuildCommand m => m.UnitId, _ => null,
                    };
                    int? building = c switch { ProduceCommand m => m.BuildingId, SetRallyCommand m => m.BuildingId, _ => null };
                    if (unit is int u) Assert.Equal(ai.Player, sim.State.Units.Single(x => x.Id == u).Owner);
                    if (building is int b) Assert.Equal(ai.Player, sim.State.Buildings.Single(x => x.Id == b).Owner);
                    commands.Add(c);
                    checkedCount++;
                }
            sim.Tick(commands);
        }
        Assert.True(checkedCount > 100);
    }

    [Fact]
    public void Same_seed_same_match()
    {
        (int, int) Run()
        {
            var sim = MainMap.Load(7);
            Play(sim, [new Commander(sim, Blue), new Commander(sim, Red)], 30 * 60);
            return (sim.State.Tick, sim.State.Winner);
        }
        Assert.Equal(Run(), Run());
    }
}
