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
    public void Each_level_beats_the_one_below_it()
    {
        // Two seeds, each with the stronger level on either side: it should win nearly all of them.
        var pairs = new[] { (Weak: AiLevel.Easy, Strong: AiLevel.Normal) };
        var games = (from pair in pairs from seed in new uint[] { 1, 2 } from strongIsRed in new[] { false, true } select (pair, seed, strongIsRed)).ToArray();
        var won = new bool[games.Length];
        var lines = new string[games.Length];
        Parallel.For(0, games.Length, i =>
        {
            var (pair, seed, strongIsRed) = games[i];
            var sim = MainMap.Load(seed);
            int strong = strongIsRed ? Red : Blue;
            Play(sim, [new Commander(sim, Blue, strongIsRed ? pair.Weak : pair.Strong), new Commander(sim, Red, strongIsRed ? pair.Strong : pair.Weak)], 30 * 60);
            won[i] = sim.State.GameOver && sim.State.Winner == strong;
            lines[i] = $"{pair.Strong} (p{strong}) vs {pair.Weak}, seed {seed}: {(sim.State.GameOver ? $"p{sim.State.Winner} won at {sim.State.Tick / T} s" : "no end")}";
        });
        foreach (var line in lines) output.WriteLine(line);
        for (int p = 0; p < pairs.Length; p++)
            Assert.True(won.Where((_, i) => games[i].pair == pairs[p]).Count(w => w) >= 3, $"{pairs[p].Strong} beats {pairs[p].Weak}");
    }

    [Fact]
    public void Match_stats_add_up()
    {
        // A match with a road break in it, to check who's credited: only artillery breaks road, and not every
        // match has one.
        Simulation sim = null!;
        MatchStats stats = null!;
        int produced = 0, broken = 0, brokenByPlayers = 0;
        foreach (uint seed in new uint[] { 3, 1, 2, 4, 5, 6 })
        {
            (sim, produced, broken, brokenByPlayers) = (MainMap.Load(seed), 0, 0, 0);
            stats = new MatchStats(sim);
            Play(sim, [new Commander(sim, Blue), new Commander(sim, Red)], 30 * 60, (_, events) =>
            {
                stats.Observe(sim, events);
                foreach (var e in events)
                {
                    if (e.Kind == SimEventKind.UnitProduced) produced++;
                    if (e.Kind != SimEventKind.SegmentBroken) continue;
                    broken++;
                    if (sim.State.Belts[e.Id].Segments[e.Index].BrokenBy >= 0) brokenByPlayers++;
                }
            });
            stats.Finish(sim.State);
            if (broken > 0) break;
        }

        Assert.Equal(produced, stats.Sides.Sum(s => s.Trained.Values.Sum()));
        Assert.Equal(brokenByPlayers, stats.Sides.Sum(s => s.Breaks));
        Assert.True(broken > 0 && brokenByPlayers == broken, "every break here is someone's shot");
        Assert.All(stats.Sides, s => Assert.InRange(s.BreaksCuttingEnemy + s.BreaksCuttingOwn, 0, s.Breaks));
        Assert.Equal(sim.State.GameOver ? sim.State.Winner : Player.None, stats.Winner);
        foreach (var (side, p) in stats.Sides.Select((s, p) => (s, p)))
        {
            Assert.Equal(sim.State.Players[p].Gathered, side.Samples[^1].Gathered);
            Assert.Equal(side.Samples.Count, side.Samples.Select(s => s.Tick).Distinct().Count());
            Assert.All(side.Samples.SkipLast(1), s => Assert.Equal(0, s.Tick % (int)(MatchStats.SampleSeconds * T)));
            Assert.Contains(stats.Firsts, f => f.Player == p && f.Type == "gatherer_post");
        }
        // Firsts come in the order they happened.
        Assert.Equal(stats.Firsts.Select(f => f.Tick).Order(), stats.Firsts.Select(f => f.Tick));
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
                        BuildCommand m => m.UnitId, ResumeBuildCommand m => m.UnitId, MendCommand m => m.UnitId,
                        RepairCommand m => m.UnitId, GarrisonCommand m => m.UnitId, _ => null,
                    };
                    int? building = c switch
                    {
                        ProduceCommand m => m.BuildingId, SetRallyCommand m => m.BuildingId, ExitCommand m => m.BuildingId, _ => null,
                    };
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
    public void Holds_the_house_on_its_half_and_guards_its_depots_with_turrets()
    {
        var sim = MainMap.Load();
        var ais = new[] { new Commander(sim, Blue), new Commander(sim, Red) };
        Play(sim, ais, 8 * 60);
        output.WriteLine(Summary(sim, Blue) + " / " + Summary(sim, Red));

        var s = sim.State;
        foreach (int player in (int[])[Blue, Red])
        {
            var home = s.Buildings.First(b => b.Owner == player && b.Type.Id == "hq").Position;
            var house = s.Buildings.Where(b => b.Type.Kind == BuildingKind.Garrison).MinBy(b => System.Numerics.Vector3.Distance(b.Position, home))!;
            Assert.Equal(player, house.Owner);
            Assert.Contains(s.Units, u => u.Owner == player && u.Inside == house.Id);
            Assert.Contains(s.Units, u => u.Owner == player && u.Type == "turret");
        }
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
