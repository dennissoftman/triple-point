namespace Sim;

/// <summary>
/// One match in numbers, for playtest reports: per player, totals, a sample every SampleSeconds, and the
/// order things were first built and trained in. The host feeds it after each tick (it's not part of
/// the tick, and it holds no text). It keeps who owned what by id, so events about things already
/// gone can still be put down to their owner.
/// </summary>
public sealed class MatchStats
{
    public const float SampleSeconds = 30;
    static readonly int SampleTicks = (int)(SampleSeconds * Simulation.TicksPerSecond);

    /// <summary>A player at a moment: packages in hand, where they came from so far, posts, and its army.</summary>
    public readonly record struct Sample(int Tick, int Packages, int Gathered, int Collected, int Spent, int Posts, int Fighters, int ArmyValue, int Buildings);

    /// <summary>The first time a player finished a building type or trained a unit type.</summary>
    public readonly record struct First(int Tick, int Player, string Type, bool Unit);

    public sealed class Side
    {
        public readonly List<Sample> Samples = [];
        public readonly Dictionary<string, int> Trained = [];
        public int UnitsLost, ValueLost;       // units dead, and what they cost
        public int PostsBuilt, PostsLost, BuildingsBuilt, BuildingsLost; // buildings built include defenses
        public int Breaks;                     // belt segments its shots broke
        public int BreaksCuttingEnemy;         // of those, ones upstream of an enemy post
        public int BreaksCuttingOwn;           // ones upstream of its own post
        public int CutOff;                     // breaks by anyone upstream of its posts
        public int Repaired;                   // segments repaired upstream of its posts
    }

    public readonly List<Side> Sides = [];
    public readonly List<First> Firsts = [];
    public int PackagesLostAtEnds => _lostAtEnds;
    public int EndTick { get; private set; } = -1;   // -1 while it's running
    public int Winner { get; private set; } = Player.None;

    readonly Dictionary<int, (int Owner, string Type)> _units = [], _buildings = [];
    readonly Dictionary<int, int> _posts = [];
    readonly Dictionary<string, int> _costs = [];
    int _lostAtEnds, _nextSample = SampleTicks;

    public MatchStats(Simulation sim)
    {
        foreach (var b in sim.BuildingTypes.Values)
            foreach (var u in b.Units) _costs[u.Id] = u.Cost;
        for (int p = 0; p < sim.State.Players.Count; p++) Sides.Add(new Side());
        Remember(sim.State);
    }

    /// <summary>Who owned a unit, building or post, by id, if it ever existed while this watched.</summary>
    public int OwnerOf(int id) =>
        _units.TryGetValue(id, out var u) ? u.Owner : _buildings.TryGetValue(id, out var b) ? b.Owner : _posts.TryGetValue(id, out int p) ? p : Player.None;

    /// <summary>A unit's or building's type id, if known ("post" for gatherer posts).</summary>
    public string TypeOf(int id) =>
        _units.TryGetValue(id, out var u) ? u.Type : _buildings.TryGetValue(id, out var b) ? b.Type : _posts.ContainsKey(id) ? "post" : "";

    /// <summary>After each tick, with the events it returned.</summary>
    public void Observe(Simulation sim, IReadOnlyList<SimEvent> events)
    {
        var state = sim.State;
        if (EndTick >= 0) return; // over
        Remember(state);
        foreach (var e in events)
            switch (e.Kind)
            {
                case SimEventKind.UnitProduced when OwnerOf(e.Index) is int p and >= 0:
                    string type = TypeOf(e.Id);
                    var trained = Sides[p].Trained;
                    if (!trained.ContainsKey(type)) Firsts.Add(new First(state.Tick, p, type, Unit: true));
                    trained[type] = trained.GetValueOrDefault(type) + 1;
                    break;
                case SimEventKind.UnitDied when OwnerOf(e.Id) is int p and >= 0:
                    Sides[p].UnitsLost++;
                    Sides[p].ValueLost += _costs.GetValueOrDefault(TypeOf(e.Id));
                    break;
                case SimEventKind.GathererDestroyed when OwnerOf(e.Id) is int p and >= 0: Sides[p].PostsLost++; break;
                case SimEventKind.BuildingDestroyed when OwnerOf(e.Id) is int p and >= 0: Sides[p].BuildingsLost++; break;
                case SimEventKind.BuildingCompleted when OwnerOf(e.Id) is int p and >= 0:
                    string built = TypeOf(e.Id);
                    if (_posts.ContainsKey(e.Index)) Sides[p].PostsBuilt++;
                    else Sides[p].BuildingsBuilt++; // defenses too, though they're lost as units
                    bool first = true;
                    foreach (var f in Firsts) if (f.Player == p && !f.Unit && f.Type == built) first = false;
                    if (first) Firsts.Add(new First(state.Tick, p, built, Unit: false));
                    break;
                case SimEventKind.SegmentBroken:
                    int by = state.Belts[e.Id].Segments[e.Index].BrokenBy;
                    if (by >= 0 && by < Sides.Count) Sides[by].Breaks++;
                    for (int v = 0; v < Sides.Count; v++)
                    {
                        if (!sim.FeedsPostOf(v, e.Id, e.Index)) continue;
                        Sides[v].CutOff++;
                        if (by == v) Sides[by].BreaksCuttingOwn++;
                        else if (by >= 0 && by < Sides.Count) Sides[by].BreaksCuttingEnemy++;
                    }
                    break;
                case SimEventKind.SegmentRepaired:
                    for (int v = 0; v < Sides.Count; v++) if (sim.FeedsPostOf(v, e.Id, e.Index)) Sides[v].Repaired++;
                    break;
                case SimEventKind.GameOver:
                    (EndTick, Winner) = (state.Tick, e.Id);
                    break;
            }
        _lostAtEnds = 0;
        foreach (var line in state.Belts) _lostAtEnds += line.Lost;
        if (state.Tick >= _nextSample || EndTick == state.Tick) TakeSample(state);
    }

    /// <summary>A last sample, for a match that stops without a game over (the window closed).</summary>
    public void Finish(SimState state)
    {
        if (EndTick < 0) EndTick = state.Tick;
        foreach (var side in Sides)
            if (side.Samples.Count > 0 && side.Samples[^1].Tick == state.Tick) return;
        TakeSample(state);
    }

    void TakeSample(SimState state)
    {
        _nextSample = state.Tick + SampleTicks - state.Tick % SampleTicks;
        for (int p = 0; p < Sides.Count; p++)
        {
            var player = state.Players[p];
            int posts = 0, fighters = 0, army = 0, buildings = 0;
            foreach (var g in state.Gatherers) if (g.Owner == p) posts++;
            foreach (var b in state.Buildings) if (b.Owner == p && b.Built) buildings++;
            foreach (var u in state.Units)
                if (u.Owner == p && u.Damage > 0 && u.Movement != Movement.Static)
                    (fighters, army) = (fighters + 1, army + _costs.GetValueOrDefault(u.Type));
            Sides[p].Samples.Add(new Sample(state.Tick, player.Packages, player.Gathered, player.Collected, player.Spent, posts, fighters, army, buildings));
        }
    }

    void Remember(SimState state)
    {
        foreach (var u in state.Units) _units.TryAdd(u.Id, (u.Owner, u.Type));
        foreach (var b in state.Buildings) _buildings.TryAdd(b.Id, (b.Owner, b.Type.Id));
        foreach (var g in state.Gatherers) _posts.TryAdd(g.Id, g.Owner);
    }
}
