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

    [Fact]
    public void Mirror_matches_end_with_either_side_winning()
    {
        // The map is point-symmetric and both sides run the same commander, so which side wins should come
        // down to the seed. One side winning nearly all means something treats the two halves differently:
        // so far, ties broken by map direction or by unit id, the commanders thinking a tick apart, and
        // whoever acts first in a tick shooting first. Each seed plays twice, once with Red's base first
        // in the sim's lists, so acting first in a tick can't hide behind one order.
        var seeds = Enumerable.Range(1, 16).ToArray();
        var results = new (int Seconds, int Winner, int Broken, int Collected)[seeds.Length];
        Parallel.For(0, seeds.Length, i =>
        {
            var sim = MainMap.Load((uint)(seeds[i] + 1) / 2, redFirst: seeds[i] % 2 == 0);
            int broken = 0, collected = 0;
            Play(sim, [new Commander(sim, Blue), new Commander(sim, Red)], 30 * 60, (_, events) =>
            {
                foreach (var e in events)
                {
                    if (e.Kind == SimEventKind.SegmentBroken) broken++;
                    if (e.Kind == SimEventKind.PickupCollected) collected++;
                }
            });
            results[i] = (sim.State.GameOver ? sim.State.Tick / T : -1, sim.State.Winner, broken, collected);
        });
        for (int i = 0; i < seeds.Length; i++)
            output.WriteLine($"seed {(seeds[i] + 1) / 2}{(seeds[i] % 2 == 0 ? ", Red first" : "")}: {results[i].Seconds} s, winner {results[i].Winner}, {results[i].Broken} segments broken, {results[i].Collected} spilled packages picked up");

        // The scripted commander can stall when both sides are broke with no posts left (the behaviour tree
        // replaces it); nearly every match still ends.
        Assert.True(results.Count(r => r.Seconds > 0) >= seeds.Length - 2, "nearly every match ends within 30 minutes");
        Assert.True(results.Sum(r => r.Broken) > seeds.Length, "belts get fought over");
        Assert.True(results.Count(r => r.Winner == Blue) >= 4 && results.Count(r => r.Winner == Red) >= 4,
            $"Blue won {results.Count(r => r.Winner == Blue)} of {seeds.Length}");
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
