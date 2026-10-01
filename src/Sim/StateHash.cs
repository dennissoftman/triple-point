using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sim;

/// <summary>
/// A fingerprint of the whole simulation, by section, so two machines in lockstep can compare states
/// cheaply and, when they differ, say where first. Every field that can affect what happens next is in
/// one section (StateHashTests changes each field in turn and checks the hash notices). Floats go in by
/// their bits: -0 and 0 differ, as they would later.
/// </summary>
public readonly record struct StateHash(int Tick, ulong Sim, ulong Players, ulong Units, ulong Roads, ulong Trucks,
    ulong Buildings, ulong Depots, ulong Shots, ulong Vision)
{
    public static readonly string[] Sections = ["sim", "players", "units", "roads", "trucks", "buildings", "depots", "shots", "vision"];

    public ulong Section(int i) => i switch
    {
        0 => Sim, 1 => Players, 2 => Units, 3 => Roads, 4 => Trucks, 5 => Buildings, 6 => Depots, 7 => Shots, _ => Vision,
    };

    /// <summary>Every section in one.</summary>
    public ulong All
    {
        get
        {
            var h = new StateHasher();
            h.Add(Tick);
            for (int i = 0; i < Sections.Length; i++) h.Add(Section(i));
            return h.Value;
        }
    }

    /// <summary>The sections that differ from another hash of the same tick, by name; empty if none.</summary>
    public string Differences(StateHash other)
    {
        var names = new List<string>();
        if (Tick != other.Tick) names.Add("tick");
        for (int i = 0; i < Sections.Length; i++)
            if (Section(i) != other.Section(i)) names.Add(Sections[i]);
        return string.Join(", ", names);
    }
}

/// <summary>A running 64-bit hash of values in order (a multiply-rotate mix of each word; not cryptographic).</summary>
public struct StateHasher
{
    ulong _h;

    public StateHasher() => _h = 0x9E3779B97F4A7C15;

    public readonly ulong Value
    {
        get
        {
            ulong x = _h; // murmur3's finalizer
            x ^= x >> 33; x *= 0xff51afd7ed558ccd;
            x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53;
            return x ^ (x >> 33);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ulong v) => _h = BitOperations.RotateLeft((_h ^ v) * 0x100000001B3, 29) + 0x9E3779B97F4A7C15;

    public void Add(long v) => Add((ulong)v);
    public void Add(int v) => Add((ulong)(uint)v);
    public void Add(uint v) => Add((ulong)v);
    public void Add(bool v) => Add(v ? 1UL : 2UL);
    public void Add(float v) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(v));
    public void Add(Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }

    public void Add(string? s)
    {
        if (s is null) { Add(0x5EED5EEDUL); return; }
        Add(s.Length);
        foreach (char c in s) Add((ulong)c);
    }

    public void Add(string[]? strings)
    {
        if (strings is null) { Add(0x5EED5EEDUL); return; }
        Add(strings.Length);
        foreach (var s in strings) Add(s);
    }

    public void Add(Against? a)
    {
        if (a is null) { Add(0x5EED5EEDUL); return; }
        Add(a.Infantry); Add(a.Vehicle); Add(a.Structure);
    }

    public void Add(in Order o)
    {
        Add((int)o.Kind); Add(o.Target); Add(o.Line); Add(o.Segment); Add(o.TargetId); Add(o.Structure); Add(o.Facing);
    }
}

public sealed partial class Simulation
{
    /// <summary>
    /// The state's fingerprint now (between ticks). Allocates nothing once the fog's grids exist. Caches
    /// rebuilt from the state (the nav grid, the paved-road grid, their dirty flags) are left out, and the
    /// fog's grids are made first if nothing has asked for them yet: neither changes what happens next, and
    /// a view's queries may touch both on one machine and not the other.
    /// </summary>
    public StateHash Hash()
    {
        EnsureVisions();
        var s = State;
        var sim = new StateHasher();
        sim.Add(s.Tick); sim.Add(_random.State); sim.Add(_nextId); sim.Add(s.GameOver); sim.Add(s.Winner);
        sim.Add(EndConditions); sim.Add(FogOfWar);
        foreach (var o in s.Obstacles) { sim.Add(o.Position); sim.Add(o.HalfWidth); sim.Add(o.HalfDepth); sim.Add(o.Heading); }
        sim.Add(_paths.Count);
        foreach (int slot in _freePaths) sim.Add(slot);

        var players = new StateHasher();
        foreach (var p in s.Players)
        {
            players.Add(p.Index); players.Add(p.Packages); players.Add(p.Gathered); players.Add(p.Collected); players.Add(p.Spent);
            players.Add(p.GraceTicksLeft); players.Add(p.GracePaused); players.Add(p.Lost);
        }

        var units = new StateHasher();
        foreach (var u in s.Units) AddUnit(ref units, u);

        var roads = new StateHasher();
        var trucks = new StateHasher();
        foreach (var line in s.Belts)
        {
            roads.Add(line.Length); roads.Add(line.Speed); roads.Add(line.Spacing); roads.Add(line.SpillLoss); roads.Add(line.TruckHealth);
            roads.Add(line.SpawnIntervalTicks); roads.Add(line.Load); roads.Add(line.StartFull); roads.Add(line.Supply); roads.Add(line.Reserve);
            roads.Add(line.Spawned); roads.Add(line.Lost); roads.Add(line.Returned); roads.Add(line.Spilled); roads.Add(line.Destroyed);
            roads.Add(line.BlockedSpawns); roads.Add(line.TrucksDestroyed); roads.Add(line.OpenStart); roads.Add(line.OpenEnd); roads.Add(line.TicksUntilSpawn);
            foreach (var seg in line.Segments)
            {
                var c = seg.Curve;
                roads.Add(c.P0); roads.Add(c.P1); roads.Add(c.P2); roads.Add(c.P3); roads.Add(c.Length);
                roads.Add(seg.Start); roads.Add(seg.MaxHealth); roads.Add(seg.Health); roads.Add((int)seg.State); roads.Add(seg.Covered);
                roads.Add(seg.Paved); roads.Add(seg.PaveProgress); roads.Add(seg.PaveCredit); roads.Add(seg.RepairCredit); roads.Add(seg.BrokenBy);
            }
            trucks.Add(line.Packages.Count);
            foreach (var t in line.Packages)
            {
                trucks.Add(t.Id); trucks.Add(t.Segment); trucks.Add(t.Distance); trucks.Add(t.PrevDistance); trucks.Add(t.Position);
                trucks.Add(t.PrevPosition); trucks.Add(t.Direction); trucks.Add(t.Cargo); trucks.Add(t.Health); trucks.Add(t.MaxHealth);
                trucks.Add(t.StoppedUntilTick); trucks.Add(t.LastDepot); trucks.Add(t.Blocked);
            }
        }

        var buildings = new StateHasher();
        foreach (var b in s.Buildings)
        {
            buildings.Add(b.Id); buildings.Add(b.Owner); buildings.Add(b.Type.Id); buildings.Add(b.Occupants); buildings.Add(b.Position);
            buildings.Add(b.Heading); buildings.Add(b.Health); buildings.Add(b.Progress); buildings.Add(b.Paid); buildings.Add(b.Stalled);
            buildings.Add(b.Rally); buildings.Add(b.Produced); buildings.Add(b.Built); buildings.Add(b.BuildProgress); buildings.Add(b.BuildPaid);
            buildings.Add(b.BuildStalled); buildings.Add(b.WorkedTick);
            buildings.Add(b.Queue.Count);
            foreach (var t in b.Queue) buildings.Add(t.Id);
        }

        var depots = new StateHasher();
        foreach (var g in s.Gatherers)
        {
            depots.Add(g.Id); depots.Add(g.Owner); depots.Add(g.Position); depots.Add(g.Line); depots.Add(g.Distance); depots.Add(g.Health);
            depots.Add(g.MaxHealth); depots.Add(g.Sight); depots.Add(g.LastGrabTick); depots.Add(g.Gathered); depots.Add(g.Share);
        }

        var shots = new StateHasher();
        foreach (var p in s.Projectiles)
        {
            shots.Add(p.Id); shots.Add(p.Owner); shots.Add(p.Shooter); shots.Add(p.TargetId); shots.Add(p.Line); shots.Add(p.Segment);
            shots.Add(p.Position); shots.Add(p.PrevPosition); shots.Add(p.Target); shots.Add(p.Origin); shots.Add(p.Speed); shots.Add(p.Damage);
            shots.Add(p.SplashRadius); shots.Add(p.Against); shots.Add(p.Ballistic);
        }
        shots.Add(0xB1C4UL);
        foreach (var p in s.Pickups)
        {
            shots.Add(p.Id); shots.Add(p.Position); shots.Add(p.From); shots.Add(p.Line); shots.Add(p.Segment); shots.Add(p.Slot);
            shots.Add(p.SpilledAtTick); shots.Add(p.LandsAtTick); shots.Add(p.Smashed);
        }

        var vision = new StateHasher();
        vision.Add(_visionTick); vision.Add(_visionUpdates);
        foreach (var v in _visions)
        {
            foreach (int seen in v.Seen) vision.Add(seen);
            vision.Add(v.Ghosts.Count);
            foreach (var g in v.Ghosts)
            {
                vision.Add(g.Id); vision.Add(g.Owner); vision.Add(g.IsPost); vision.Add(g.Type?.Id); vision.Add(g.Position); vision.Add(g.Heading);
                vision.Add(g.Built); vision.Add(g.HealthShare); vision.Add(g.Line); vision.Add(g.Distance); vision.Add(g.SeenTick); vision.Add(g.Confirmed);
            }
            foreach (var states in v.SegmentStates) foreach (var st in states) vision.Add((int)st);
            foreach (var healths in v.SegmentHealth) foreach (float h in healths) vision.Add(h);
        }

        return new StateHash(s.Tick, sim.Value, players.Value, units.Value, roads.Value, trucks.Value, buildings.Value, depots.Value, shots.Value, vision.Value);
    }

    void AddUnit(ref StateHasher h, in Unit u)
    {
        h.Add(u.Id); h.Add(u.Owner); h.Add(u.Type); h.Add(u.Position); h.Add(u.PrevPosition); h.Add(u.Heading); h.Add(u.PrevHeading);
        h.Add(u.Turret); h.Add(u.PrevTurret); h.Add(u.Speed); h.Add((int)u.Movement); h.Add(u.Acceleration); h.Add(u.Braking);
        h.Add(u.EaseIn); h.Add(u.EaseOut); h.Add(u.TurnRate); h.Add(u.TurretTurnRate); h.Add(u.TurretArc); h.Add(u.ReverseSpeed);
        h.Add(u.CurrentSpeed); h.Add(u.Effort); h.Add(u.TurnSpeed); h.Add(u.Driving); h.Add(u.MaxMembers); h.Add(u.MemberHealth);
        h.Add(u.Health); h.Add((int)u.WeaponKind); h.Add(u.Damage); h.Add(u.Range); h.Add(u.ShellSpeed); h.Add(u.SplashRadius);
        h.Add(u.MinRange); h.Add(u.Scatter); h.Add(u.Ballistic); h.Add(u.Against); h.Add((int)u.Prefers); h.Add(u.StopsToFire);
        h.Add(u.ReloadTicks); h.Add(u.ReadyAtTick); h.Add(u.LastShotTick); h.Add(u.Firing); h.Add(u.FireAt); h.Add(u.LastAttacker);
        h.Add(u.LastHitTick); h.Add(u.RespondTo); h.Add(u.GaveUpOn); h.Add(u.Builds); h.Add(u.RepairSeconds); h.Add(u.RepairCost);
        h.Add(u.Anchor); h.Add(u.Returning); h.Add(u.Holding); h.Add(u.Inside); h.Add(u.InsideReach); h.Add(u.RoadBoost);
        h.Add(u.AutoRetreat); h.Add(u.Cost); h.Add(u.RepairCredit); h.Add(u.Current); h.Add(u.Radius); h.Add(u.Sight);
        h.Add(u.RevealSince); h.Add(u.RevealUntil); h.Add(u.RevealMask); h.Add(u.PathSlot); h.Add(u.RestGoal);
        h.Add(u.Pending?.Count ?? -1);
        if (u.Pending is not null) foreach (var o in u.Pending) h.Add(o);
        if (u.PathSlot >= 0 && u.PathSlot < _paths.Count)
        {
            var path = _paths[u.PathSlot];
            h.Add(path.Next); h.Add(path.Version); h.Add(path.Goal); h.Add(path.Planned); h.Add(path.Points.Count);
            foreach (var p in path.Points) h.Add(p);
        }
    }
}
