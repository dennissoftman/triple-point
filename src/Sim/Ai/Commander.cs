using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim.Ai;

/// <summary>
/// A computer player that fights over the belt. Every ThinkTicks it looks at the world (through its
/// AiView) and returns the commands a player would give: the same objects, checked the same way.
///
/// - Economy: keeps builders, takes post spots on its side of the map (nearest home first), puts up a
///   barracks and a factory, and trains a mix of units, one at a time per building.
/// - Defense: an enemy near its posts or buildings draws the army.
/// - Raids: with a few units it goes for the enemy's most exposed post, and has one unit break the
///   enemy's belt just upstream of their posts where that doesn't cut its own.
/// - The push: clearly stronger, it attack-moves on the enemy's base.
/// - Damaged units pull back home; repairers mend broken belt that feeds its posts.
/// - Spilled packages where no enemy stands get picked up by whoever is idle nearest.
/// - Under fog of war it knows only what its side sees and remembers (AiView), and sends its fastest
///   fighter to look along the enemy's half of the belt wherever it hasn't looked for a while.
///
/// Deterministic: no randomness, and it thinks on fixed ticks, the same ones for every player: thinking a
/// tick apart handed one side a steady edge in mirror matches. Nothing it keeps allocates once warm.
/// </summary>
public sealed class Commander
{
    public const int ThinkTicks = 10;               // twice a second, on ticks that are multiples of it
    const float PostSearchStep = 4f;                // m along free belt between the post spots it weighs
    const float ThreatRadius = 30f;                 // m around its posts and buildings that counts as under attack
    const float SpotDangerRadius = 22f;             // m: no post where enemy fighters stand this close
    const float RetreatHealth = 0.3f;               // share of full health below which a unit pulls back
    const float OrderSlack = 6f;                    // m: an order to within this of the current one isn't re-issued
    const float StagingDistance = 22f;              // m from home toward the enemy, where the army gathers
    const int RaidSize = 4, PushSize = 8;           // fighters before it raids, and before it pushes
    const float PushRatio = 1.5f;                   // its strength over the enemy's before it pushes
    const int WantedEngineers = 1, WantedBuilders = 2;
    const float BuildClearance = 8f;                // m added to a new building's side when it looks for room: 4 m between buildings, so tanks get out
    const float PostWorthMin = 6f;                  // packages to keep in hand beyond a post's cost, once it has two posts
    const float CollectReach = 45f;                 // m an idle unit goes out of its way for a spilled package
    const float CollectDanger = 20f;                // m: no package this close to enemy fighters is worth fetching
    const int CollectorsPerThink = 3;
    const float ScoutStaleSeconds = 45f;            // belt it hasn't seen for this long is worth a look

    readonly Simulation _sim;
    readonly AiView _view;
    readonly int _me;
    readonly List<Command> _out = [];

    // What it sees this think; kept between thinks only to be reused.
    readonly List<int> _builders = [], _engineers = [], _army = [];
    readonly List<int> _enemyFighters = [];
    readonly List<(int Line, float From, float To)> _spots = [];
    readonly HashSet<int> _retreating = [];
    readonly HashSet<int> _ordered = [];            // units given an order this think
    readonly Predicate<int> _gone;
    int _scout = -1;                                // the unit out looking, if any
    Vector3 _home, _enemyHome, _staging;
    float _strength, _enemyStrength;

    public Commander(Simulation sim, int player)
    {
        (_sim, _me) = (sim, player);
        _view = new AiView(sim, player);
        _gone = id => !Alive(id);
    }

    public int Player => _me;

    /// <summary>The commands for the coming tick: on a think tick, whatever it decided; otherwise none.</summary>
    public IReadOnlyList<Command> Think()
    {
        _out.Clear();
        var state = _sim.State;
        if (state.GameOver || state.Players[_me].Lost || state.Tick % ThinkTicks != 0) return _out;
        Survey();
        Build();
        Produce();
        Fight();
        Mend();
        Collect();
        Scout();
        return _out;
    }

    // ---- What there is ----

    void Survey()
    {
        var state = _sim.State;
        _builders.Clear(); _engineers.Clear(); _army.Clear(); _enemyFighters.Clear();
        (_strength, _enemyStrength) = (0, 0);
        var units = state.Units;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u.Owner == _me)
            {
                if (u.Builds is not null) _builders.Add(i);
                else if (u.Damage <= 0 && u.RepairSeconds > 0) _engineers.Add(i);
                else if (u.Damage > 0 && u.Movement != Movement.Static)
                {
                    _army.Add(i);
                    if (!_retreating.Contains(u.Id)) _strength += Strength(u);
                }
            }
            else if (u.Owner != Sim.Player.None && u.Damage > 0 && _view.SeesUnit(u))
            {
                _enemyFighters.Add(i);
                _enemyStrength += Strength(u);
            }
        }
        _retreating.RemoveWhere(_gone);
        if (_scout >= 0 && !Alive(_scout)) _scout = -1;

        _home = Home(out bool _);
        _enemyHome = EnemyHome();
        var toward = Flat(_enemyHome - _home);
        _staging = toward.LengthSquared() > 1 ? _home + Vector3.Normalize(toward) * StagingDistance : _home;
    }

    static float Strength(in Unit u) => u.Dps * u.Health / 100;

    bool Alive(int id)
    {
        foreach (var u in _sim.State.Units) if (u.Id == id) return true;
        return false;
    }

    // Its first building that trains builders (an HQ), else any building, else its first unit.
    Vector3 Home(out bool found)
    {
        found = true;
        Vector3? any = null;
        foreach (var b in _sim.State.Buildings)
        {
            if (b.Owner != _me) continue;
            if (TrainsBuilders(b.Type)) return b.Position;
            any ??= b.Position;
        }
        if (any is Vector3 a) return a;
        foreach (var u in _sim.State.Units) if (u.Owner == _me) return u.Position;
        found = false;
        return Vector3.Zero;
    }

    // The nearest enemy home it knows of (a remembered building that trains builders, else any remembered
    // building); failing that, across the map from its own.
    Vector3 EnemyHome()
    {
        float best = float.MaxValue, bestAny = float.MaxValue;
        Vector3? home = null, any = null;
        foreach (var g in _view.Ghosts)
        {
            if (g.IsPost || g.Type is null || _sim.State.Players[g.Owner].Lost) continue;
            float d = Vector3.DistanceSquared(g.Position, _home);
            if (TrainsBuilders(g.Type) && d < best) (best, home) = (d, g.Position);
            if (d < bestAny) (bestAny, any) = (d, g.Position);
        }
        return home ?? any ?? -_home;
    }

    static bool TrainsBuilders(BuildingType type)
    {
        foreach (var t in type.Units) if (t.Builds is not null) return true;
        return false;
    }

    // ---- Building ----

    void Build()
    {
        var state = _sim.State;
        var units = CollectionsMarshal.AsSpan(state.Units);
        foreach (int i in _builders)
        {
            ref var builder = ref units[i];
            if (builder.Current.Kind != UnitOrder.None || builder.Pending.Count > 0) continue;

            // An unfinished foundation of its own that nobody works on first.
            if (Abandoned() is int site) { _out.Add(new ResumeBuildCommand(_me, builder.Id, site)); continue; }
            if (NextBuilding(builder) is not BuildingType type || !_sim.CanAfford(_me, type)) continue;
            if (type.Kind == BuildingKind.Post)
            {
                if (PostSite(type, out var at, out float heading)) _out.Add(new BuildCommand(_me, builder.Id, type.Id, at, heading));
            }
            else if (BaseSite(type, out var at, out float heading)) _out.Add(new BuildCommand(_me, builder.Id, type.Id, at, heading));
            // one new building per think: the next builder sees this one as planned
            if (_out.Count > 0 && _out[^1] is BuildCommand) break;
        }
    }

    int? Abandoned()
    {
        var state = _sim.State;
        foreach (var b in state.Buildings)
        {
            if (b.Owner != _me || b.Built || b.WorkedTick >= state.Tick - 2 * ThinkTicks) continue;
            bool someoneOnIt = false;
            foreach (int i in _builders)
                if (state.Units[i].Current.Kind == UnitOrder.Build && state.Units[i].Current.TargetId == b.Id) someoneOnIt = true;
            if (!someoneOnIt) return b.Id;
        }
        return null;
    }

    // What to put up next: two posts, a barracks, a factory, then more posts while there's room.
    BuildingType? NextBuilding(in Unit builder)
    {
        if (builder.Builds is null) return null;
        BuildingType? post = null, barracks = null, factory = null;
        foreach (var id in builder.Builds)
        {
            if (!_sim.BuildingTypes.TryGetValue(id, out var t)) continue;
            if (t.Kind == BuildingKind.Post) post ??= t;
            else if (t.Kind == BuildingKind.Building && t.Units.Length > 0)
            {
                bool vehicles = false;
                foreach (var u in t.Units) if (u.Movement is Movement.Wheeled or Movement.Tracked) vehicles = true;
                if (vehicles) factory ??= t; else barracks ??= t;
            }
        }
        int posts = PostCount();
        if (post is not null && posts < 2 && !Planned(post)) return post;
        if (barracks is not null && Count(barracks) == 0 && !Planned(barracks)) return barracks;
        if (factory is not null && Count(factory) == 0 && !Planned(factory) && Built(barracks)) return factory;
        if (post is not null && !Planned(post) && _sim.State.Players[_me].Packages >= post.Cost + PostWorthMin) return post;
        return null;
    }

    int PostCount()
    {
        int n = 0;
        foreach (var g in _sim.State.Gatherers) if (g.Owner == _me) n++;
        foreach (var b in _sim.State.Buildings) if (b.Owner == _me && !b.Built && b.Type.Kind == BuildingKind.Post) n++;
        return n;
    }

    int Count(BuildingType type)
    {
        int n = 0;
        foreach (var b in _sim.State.Buildings) if (b.Owner == _me && b.Type.Id == type.Id) n++;
        return n;
    }

    bool Built(BuildingType? type)
    {
        if (type is null) return false;
        foreach (var b in _sim.State.Buildings) if (b.Owner == _me && b.Type.Id == type.Id && b.Built) return true;
        return false;
    }

    // A builder already walking to put one up.
    bool Planned(BuildingType type)
    {
        foreach (int i in _builders)
            if (_sim.State.Units[i].Current is { Kind: UnitOrder.Build, Structure: string s } && s == type.Id) return true;
        return false;
    }

    // The best free post spot: on its side of the map (nearer its home than the enemy's), with no enemy
    // fighters close, nearest its home. Beside the belt on the side facing home.
    bool PostSite(BuildingType type, out Vector3 at, out float heading)
    {
        (at, heading) = (default, 0);
        _spots.Clear();
        _sim.PostSpots(_spots);
        float best = float.MaxValue;
        foreach (var (l, from, to) in _spots)
        {
            var line = _sim.State.Belts[l];
            for (float d = from + 1; d <= to - 1; d += PostSearchStep)
            {
                var p = line.PositionAt(d) with { Y = 0 };
                float mine = Vector3.Distance(p, _home), theirs = Vector3.Distance(p, _enemyHome);
                if (mine >= theirs || mine >= best || EnemyFighterNear(p, SpotDangerRadius)) continue;
                var near = p + Vector3.Normalize(Flat(_home - p) + new Vector3(1e-3f, 0, 0)) * Simulation.PostOffset;
                if (!_sim.SnapPost(near, Simulation.PostReach, out var spot, out float facing)) continue;
                if (_sim.TooCloseToPost(spot) || !_sim.CanPlace(type, spot)) continue;
                (best, at, heading) = (mine, spot, facing);
            }
        }
        return best < float.MaxValue;
    }

    // Room near home for a building, preferring the side away from the enemy, with space around it; its
    // exit faces the enemy.
    bool BaseSite(BuildingType type, out Vector3 at, out float heading)
    {
        at = default;
        var toward = Flat(_enemyHome - _home);
        var forward = toward.LengthSquared() > 1 ? Vector3.Normalize(toward) : Vector3.UnitZ;
        heading = MathF.Atan2(forward.X, forward.Z);
        var roomy = type with { Size = type.Size + BuildClearance };
        float best = float.MaxValue;
        for (float r = 12; r <= 40; r += 4)
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.Tau / 16;
                var dir = new Vector3(forward.X * MathF.Cos(a) - forward.Z * MathF.Sin(a), 0, forward.X * MathF.Sin(a) + forward.Z * MathF.Cos(a));
                float score = r + 6 * (1 + Vector3.Dot(dir, forward)); // near, and behind home rather than in front
                if (score >= best) continue;
                var spot = Simulation.SnapToGrid(_home + dir * r, type.Size);
                if (!_sim.CanPlace(roomy, spot) || !_sim.CanPlace(type, spot)) continue;
                (best, at) = (score, spot);
            }
        return best < float.MaxValue;
    }

    // ---- Training ----

    void Produce()
    {
        var state = _sim.State;
        foreach (var b in state.Buildings)
        {
            if (b.Owner != _me || !b.Built || b.Type.Units.Length == 0) continue;
            if (Vector3.Distance(b.Rally, _staging) > 3 && !TrainsBuilders(b.Type)) _out.Add(new SetRallyCommand(_me, b.Id, _staging));
            if (b.Queue.Count > 0) continue;
            if (Choose(b) is UnitType type) _out.Add(new ProduceCommand(_me, b.Id, type.Id));
        }
    }

    // What a building trains next: builders and engineers up to what it wants, else the fighter it has
    // fewest of (artillery counts double: it wants less of it). Nothing while a wanted building waits for money.
    UnitType? Choose(Building b)
    {
        UnitType? pick = null;
        float fewest = float.MaxValue;
        foreach (var t in b.Type.Units)
        {
            if (t.Builds is not null) { if (_builders.Count + Queued(t) < WantedBuilders) return t; continue; }
            if (t.Weapon is null) { if (t.RepairSeconds > 0 && _engineers.Count + Queued(t) < WantedEngineers && PostCount() > 0) return t; continue; }
            float have = (Have(t) + Queued(t)) * (t.StopsToFire ? 2 : 1);
            if (have < fewest) (fewest, pick) = (have, t);
        }
        return WaitingForMoney() ? null : pick;
    }

    int Have(UnitType type)
    {
        int n = 0;
        foreach (var u in _sim.State.Units) if (u.Owner == _me && u.Type == type.Id) n++;
        return n;
    }

    int Queued(UnitType type)
    {
        int n = 0;
        foreach (var b in _sim.State.Buildings)
            if (b.Owner == _me) foreach (var t in b.Queue) if (t.Id == type.Id) n++;
        return n;
    }

    // An idle builder wants a building it can't afford yet: save for it.
    bool WaitingForMoney()
    {
        foreach (int i in _builders)
        {
            var u = _sim.State.Units[i];
            if (u.Current.Kind != UnitOrder.None) continue;
            if (NextBuilding(u) is BuildingType t && !_sim.CanAfford(_me, t)) return true;
        }
        return false;
    }

    // ---- Fighting ----

    void Fight()
    {
        var units = CollectionsMarshal.AsSpan(_sim.State.Units);

        // The badly hurt pull back home and stay there as its guard, while it has enough healthy fighters to
        // raid without them. Nothing heals them yet, so with fewer than that, everyone fights: otherwise two
        // worn-out armies sit at home for ever.
        int healthy = 0;
        foreach (int i in _army) if (units[i].Health > units[i].MaxHealth * RetreatHealth) healthy++;
        if (healthy < RaidSize) _retreating.Clear();
        else
            foreach (int i in _army)
            {
                ref var u = ref units[i];
                if (_retreating.Contains(u.Id) || u.Health > u.MaxHealth * RetreatHealth) continue;
                _retreating.Add(u.Id);
                _out.Add(new MoveCommand(_me, u.Id, _home));
            }

        if (Threat() is Vector3 threat) { AllAttackMove(threat); return; }
        int fighters = _army.Count - _retreating.Count - (_scout >= 0 ? 1 : 0);
        if (fighters >= PushSize && _strength >= PushRatio * _enemyStrength && EnemyBase() is Vector3 target) { AllAttackMove(target); return; }
        if (fighters >= RaidSize && Raid()) return;
        AllMoveTo(_staging);
    }

    // The enemy fighter nearest one of its posts or buildings, within ThreatRadius of it.
    Vector3? Threat()
    {
        var state = _sim.State;
        float best = ThreatRadius * ThreatRadius;
        Vector3? at = null;
        foreach (int e in _enemyFighters)
        {
            var p = state.Units[e].Position;
            foreach (var g in state.Gatherers)
                if (g.Owner == _me && Vector3.DistanceSquared(g.Position, p) is float d && d < best) (best, at) = (d, p);
            foreach (var b in state.Buildings)
                if (b.Owner == _me && Vector3.DistanceSquared(b.Position, p) is float d && d < best) (best, at) = (d, p);
        }
        return at;
    }

    // The nearest enemy building it remembers.
    Vector3? EnemyBase()
    {
        float best = float.MaxValue;
        Vector3? at = null;
        foreach (var g in _view.Ghosts)
            if (!g.IsPost && Vector3.DistanceSquared(g.Position, _home) is float d && d < best) (best, at) = (d, g.Position);
        return at;
    }

    // The enemy's most exposed post (fewest of their fighters near it, then nearest home): the army goes
    // for it, and one fighter breaks their belt just upstream of their posts where that doesn't cut its own.
    bool Raid()
    {
        var state = _sim.State;
        int target = -1;
        float best = float.MaxValue;
        foreach (var g in _view.Ghosts)
        {
            if (!g.IsPost) continue;
            float score = 40 * FightersNear(g.Position, 20, enemy: true) + Vector3.Distance(g.Position, _home);
            if (score < best) (best, target) = (score, g.Id);
        }
        if (target < 0) return false;

        int breaker = BeltBreak(out int line, out int segment) ? Breaker() : -1;
        var units = _sim.State.Units;
        foreach (int i in _army)
        {
            var u = units[i];
            if (Busy(u.Id)) continue;
            if (u.Id == breaker)
            {
                if (u.Current.Kind != UnitOrder.AttackSegment) _out.Add(new AttackSegmentCommand(_me, u.Id, line, segment));
                continue;
            }
            if (u.Current.Kind != UnitOrder.Attack || u.Current.TargetId != target) _out.Add(new AttackCommand(_me, u.Id, target));
        }
        return true;
    }

    // An open, unbroken segment just upstream of an enemy post and downstream of all its own posts on that line.
    bool BeltBreak(out int line, out int segment)
    {
        var state = _sim.State;
        (line, segment) = (-1, -1);
        foreach (var g in _view.Ghosts)
        {
            if (!g.IsPost) continue;
            float mine = -1;
            foreach (var m in state.Gatherers) if (m.Owner == _me && m.Line == g.Line) mine = MathF.Max(mine, m.Distance);
            var belt = state.Belts[g.Line];
            int s = belt.SegmentAt(MathF.Max(0, g.Distance - 6));
            var seg = belt.Segments[s];
            if (seg.Covered || _view.SeenState(g.Line, s) == SegmentState.Broken || seg.Start <= mine + 2 || seg.End > g.Distance) continue;
            (line, segment) = (g.Line, s);
            return true;
        }
        return false;
    }

    // Who breaks the belt: a fighter that stops to fire (artillery) if it has one, else its first fighter.
    int Breaker()
    {
        var units = _sim.State.Units;
        int pick = -1;
        foreach (int i in _army)
        {
            var u = units[i];
            if (Busy(u.Id)) continue;
            if (u.StopsToFire) return u.Id;
            if (pick < 0) pick = u.Id;
        }
        return pick;
    }

    void AllAttackMove(Vector3 at)
    {
        var units = _sim.State.Units;
        foreach (int i in _army)
        {
            var u = units[i];
            if (Busy(u.Id)) continue;
            if (u.Current.Kind == UnitOrder.AttackMove && Vector3.Distance(u.Current.Target, at) < OrderSlack) continue;
            _out.Add(new AttackMoveCommand(_me, u.Id, at));
        }
    }

    // Idle fighters away from the staging point go back to it (attack-moving, so they fight on the way).
    void AllMoveTo(Vector3 at)
    {
        var units = _sim.State.Units;
        foreach (int i in _army)
        {
            var u = units[i];
            if (Busy(u.Id) || u.Current.Kind != UnitOrder.None || Vector3.Distance(u.Position, at) < 10) continue;
            _out.Add(new AttackMoveCommand(_me, u.Id, at));
        }
    }

    // ---- Mending ----

    // A broken segment upstream of one of its posts, with no enemy close: the nearest idle repairer goes
    // (an engineer first, a builder if it has none). Damaged belt close by, they mend on their own.
    void Mend()
    {
        var state = _sim.State;
        foreach (var g in state.Gatherers)
        {
            if (g.Owner != _me) continue;
            var belt = state.Belts[g.Line];
            for (int s = 0; s < belt.Segments.Length && belt.Segments[s].End <= g.Distance; s++)
            {
                var seg = belt.Segments[s];
                if (_view.SeenState(g.Line, s) != SegmentState.Broken) continue;
                var mid = seg.Curve.PositionAt(seg.Curve.Length / 2) with { Y = 0 };
                if (EnemyFighterNear(mid, 20) || Mending(g.Line, s)) continue;
                int repairer = IdleRepairer(mid);
                if (repairer >= 0) _out.Add(new RepairSegmentCommand(_me, repairer, g.Line, s));
                return; // one at a time
            }
        }
    }

    bool Mending(int line, int segment)
    {
        foreach (var u in _sim.State.Units)
            if (u.Owner == _me && u.Current is { Kind: UnitOrder.Repair } o && o.Line == line && o.Segment == segment) return true;
        return false;
    }

    int IdleRepairer(Vector3 near) => IdleRepairer(_engineers, near) is int e and >= 0 ? e : IdleRepairer(_builders, near);

    int IdleRepairer(List<int> among, Vector3 near)
    {
        var units = _sim.State.Units;
        int pick = -1;
        float best = float.MaxValue;
        foreach (int i in among)
        {
            var u = units[i];
            if (u.Current.Kind != UnitOrder.None || u.RepairSeconds <= 0) continue;
            float d = Vector3.DistanceSquared(u.Position, near);
            if (d < best) (best, pick) = (d, u.Id);
        }
        return pick;
    }

    // ---- Picking up ----

    // Landed packages no enemy stands near: the nearest idle unit (fighter or engineer) within reach fetches
    // one, a few per think; a unit already heading for a package counts it as taken.
    void Collect()
    {
        var state = _sim.State;
        _ordered.Clear();
        foreach (var c in _out)
            switch (c)
            {
                case MoveCommand m: _ordered.Add(m.UnitId); break;
                case AttackMoveCommand m: _ordered.Add(m.UnitId); break;
                case AttackCommand m: _ordered.Add(m.UnitId); break;
                case AttackSegmentCommand m: _ordered.Add(m.UnitId); break;
                case RepairSegmentCommand m: _ordered.Add(m.UnitId); break;
                case BuildCommand m: _ordered.Add(m.UnitId); break;
                case ResumeBuildCommand m: _ordered.Add(m.UnitId); break;
            }
        int sent = 0;
        foreach (var p in state.Pickups)
        {
            if (sent >= CollectorsPerThink) return;
            if (p.Smashed || p.LandsAtTick > state.Tick || EnemyFighterNear(p.Position, CollectDanger) || Fetching(p.Position)) continue;
            int pick = Collector(_army, p.Position, out float best);
            if (Collector(_engineers, p.Position, out float e) is int engineer and >= 0 && e < best) (pick, best) = (engineer, e);
            if (pick < 0 || best > CollectReach * CollectReach) continue;
            _out.Add(new MoveCommand(_me, pick, p.Position));
            _ordered.Add(pick);
            sent++;
        }
    }

    bool Fetching(Vector3 at)
    {
        foreach (var u in _sim.State.Units)
            if (u.Owner == _me && u.Current.Kind == UnitOrder.Move && Vector3.DistanceSquared(Flat(u.Current.Target), Flat(at)) < 1) return true;
        return false;
    }

    int Collector(List<int> among, Vector3 at, out float best)
    {
        var units = _sim.State.Units;
        int pick = -1;
        best = float.MaxValue;
        foreach (int i in among)
        {
            var u = units[i];
            if (u.Current.Kind != UnitOrder.None || u.Pending.Count > 0 || _retreating.Contains(u.Id) || _ordered.Contains(u.Id)) continue;
            float d = Vector3.DistanceSquared(Flat(u.Position), Flat(at));
            if (d < best) (best, pick) = (d, u.Id);
        }
        return pick;
    }

    // ---- Scouting ----

    // Under fog, one fighter (the fastest) goes to look at the open belt on the enemy's half where it's gone
    // longest unseen, once that's more than ScoutStaleSeconds: that's where their posts would be. It
    // rejoins the army when it gets there.
    void Scout()
    {
        if (!_view.Fogged) return;
        var state = _sim.State;
        if (_scout >= 0)
        {
            int i = IndexOf(_scout);
            if (i >= 0 && state.Units[i].Current.Kind != UnitOrder.None && !_retreating.Contains(_scout)) return;
            _scout = -1;
        }
        int stale = state.Tick - (int)(ScoutStaleSeconds * Simulation.TicksPerSecond);
        Vector3? look = null;
        int oldest = int.MaxValue;
        float nearest = float.MaxValue; // of the equally stale, the nearest home (never by which line comes first)
        foreach (var line in state.Belts)
            foreach (var seg in line.Segments)
            {
                if (seg.Covered) continue;
                var mid = seg.Curve.PositionAt(seg.Curve.Length / 2) with { Y = 0 };
                if (Vector3.Distance(mid, _enemyHome) >= Vector3.Distance(mid, _home) || EnemyFighterNear(mid, SpotDangerRadius)) continue;
                int seen = _view.LastSeen(mid);
                float d = Vector3.Distance(mid, _home);
                if (seen < stale && (seen < oldest || (seen == oldest && d < nearest))) (oldest, nearest, look) = (seen, d, mid);
            }
        if (look is not Vector3 at) return;

        var units = state.Units;
        int pick = -1;
        float fastest = 0;
        foreach (int i in _army)
        {
            var u = units[i];
            if (_retreating.Contains(u.Id) || u.StopsToFire || u.Speed <= fastest) continue;
            (fastest, pick) = (u.Speed, u.Id);
        }
        if (pick < 0) return;
        _scout = pick;
        _out.Add(new MoveCommand(_me, pick, at));
    }

    int IndexOf(int id)
    {
        var units = _sim.State.Units;
        for (int i = 0; i < units.Count; i++) if (units[i].Id == id) return i;
        return -1;
    }

    // ---- Helpers ----

    // Out of the army's hands: pulling back, or out looking.
    bool Busy(int id) => id == _scout || _retreating.Contains(id);

    bool EnemyFighterNear(Vector3 at, float radius)
    {
        foreach (int e in _enemyFighters)
            if (Vector3.DistanceSquared(Flat(_sim.State.Units[e].Position), Flat(at)) < radius * radius) return true;
        return false;
    }

    int FightersNear(Vector3 at, float radius, bool enemy)
    {
        int n = 0;
        var units = _sim.State.Units;
        foreach (int i in enemy ? _enemyFighters : _army)
            if (Vector3.DistanceSquared(Flat(units[i].Position), Flat(at)) < radius * radius) n++;
        return n;
    }

    static Vector3 Flat(Vector3 v) => v with { Y = 0 };
}
