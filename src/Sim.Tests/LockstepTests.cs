using Sim.Ai;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// Lockstep between two machines in one process, over a pretend network that delays every message by a
/// few frames (in order, as ENet's reliable channel delivers). On each machine the commander AI plays that
/// machine's side, its commands going through lockstep like a player's clicks.
/// </summary>
public class LockstepTests(ITestOutputHelper output)
{
    const int T = Simulation.TicksPerSecond, Delay = 3;

    sealed class Machine(int player, uint seed)
    {
        public readonly Simulation Sim = MainMap.Load(seed);
        public readonly Lockstep Net = new(player, 2, Delay, seed, "main");
        public Commander? Ai;
        public double Budget;
        public int Stalls;
    }

    // Messages from one machine to the other, each arriving `latency` frames (give or take `jitter`) after it
    // was sent, never before an earlier one.
    sealed class Wire(int latency, int jitter, uint seed)
    {
        readonly Queue<(int At, byte[] Message)> _queue = new();
        readonly SimRandom _random = new(seed);
        int _last;

        public void Send(int frame, byte[] message)
        {
            _last = Math.Max(_last, frame + latency + (int)_random.Range(-jitter, jitter + 1));
            _queue.Enqueue((_last, message));
        }

        public void Deliver(int frame, Lockstep to)
        {
            while (_queue.Count > 0 && _queue.Peek().At <= frame) to.Receive(_queue.Dequeue().Message);
        }
    }

    // A frame on one machine: a tick's worth of real time, and as many ticks as are due and ready
    // (catching up after a stall, a few at most).
    static void Frame(Machine m, int frame, Wire outgoing)
    {
        m.Budget = Math.Min(m.Budget + 1, 4);
        while (m.Budget >= 1)
        {
            if (!m.Net.Ready(m.Sim.State.Tick)) { m.Stalls++; break; }
            if (m.Ai is not null) foreach (var c in m.Ai.Think()) m.Net.Issue(c);
            m.Sim.Tick(m.Net.Begin(m.Sim));
            m.Budget--;
        }
        while (m.Net.Outbox.Count > 0) outgoing.Send(frame, m.Net.Outbox.Dequeue());
    }

    static (Machine A, Machine B, Wire ToA, Wire ToB) Pair(uint seed = 5, int latency = 2, int jitter = 2)
    {
        var a = new Machine(0, seed);
        var b = new Machine(1, seed);
        (a.Ai, b.Ai) = (new Commander(a.Sim, 0), new Commander(b.Sim, 1));
        return (a, b, new Wire(latency, jitter, 1), new Wire(latency, jitter, 2));
    }

    static void Run(Machine a, Machine b, Wire toA, Wire toB, int frames, ref int frame, Action<int>? each = null)
    {
        for (int end = frame + frames; frame < end; frame++)
        {
            toA.Deliver(frame, a.Net);
            toB.Deliver(frame, b.Net);
            each?.Invoke(frame);
            Frame(a, frame, toB);
            Frame(b, frame, toA);
        }
    }

    [Fact]
    public void Two_machines_play_the_same_match_over_a_laggy_network()
    {
        var (a, b, toA, toB) = Pair();
        int frame = 0;
        Run(a, b, toA, toB, 4 * 60 * T, ref frame);

        Assert.Null(a.Net.Desync);
        Assert.Null(b.Net.Desync);
        // Each machine is at most a turn's worth ahead of the other, and kept up with real time.
        Assert.InRange(a.Sim.State.Tick - b.Sim.State.Tick, -Delay, Delay);
        Assert.True(a.Sim.State.Tick > 4 * 60 * T * 0.95, $"tick {a.Sim.State.Tick} after {frame} frames ({a.Stalls} stalls)");
        // The same states: every hash both noted agrees (they were compared as they went, too).
        var theirs = b.Net.Replay.Hashes.ToDictionary(h => h.Tick);
        int compared = 0;
        foreach (var h in a.Net.Replay.Hashes)
            if (theirs.TryGetValue(h.Tick, out var o)) { Assert.Equal(h, o); compared++; }
        Assert.True(compared >= Math.Min(a.Sim.State.Tick, b.Sim.State.Tick) / Replay.DefaultHashEvery - 1, $"{compared} hashes compared"); // one a second
        // Both AIs did things, through lockstep.
        Assert.True(a.Sim.State.Units.Count(u => u.Owner == 0) > 3 && a.Sim.State.Units.Count(u => u.Owner == 1) > 3);
        // Either machine's replay plays the match back on its own.
        Assert.Null(a.Net.Replay.Verify(MainMap.Load(5)));
        Assert.Null(b.Net.Replay.Verify(MainMap.Load(5)));
        output.WriteLine($"tick {a.Sim.State.Tick} after {frame} frames; stalls {a.Stalls}/{b.Stalls}; {a.Net.Replay.Turns.Count} turns with commands");
    }

    [Fact]
    public void A_desync_stops_both_and_the_dumps_say_where()
    {
        var (a, b, toA, toB) = Pair();
        int frame = 0, victim = -1;
        Run(a, b, toA, toB, 60 * T, ref frame);
        // Machine B destroys one of Blue's units that A knows nothing about, as a CPU difference might.
        victim = b.Sim.State.Units.First(u => u.Owner == 0).Id;
        b.Net.InjectFault(new DestroyCommand(victim));
        int faultTick = b.Sim.State.Tick;
        Run(a, b, toA, toB, 5 * T, ref frame);

        Assert.NotNull(a.Net.Desync);
        Assert.NotNull(b.Net.Desync);
        var (tick, sections) = a.Net.Desync!.Value;
        Assert.Equal(b.Net.Desync!.Value.Tick, tick);
        Assert.InRange(tick, faultTick + 1, faultTick + Replay.DefaultHashEvery);
        Assert.Contains("units", sections);
        int stoppedA = a.Sim.State.Tick, stoppedB = b.Sim.State.Tick;
        Run(a, b, toA, toB, T, ref frame);
        Assert.Equal((stoppedA, stoppedB), (a.Sim.State.Tick, b.Sim.State.Tick)); // nothing runs after

        // Each side rebuilds the start and plays its own replay to the desync tick: the dumps differ at the unit.
        string Dump(Machine m) { var sim = MainMap.Load(5); m.Net.Replay.PlayTo(sim, tick); return StateDump.Write(sim); }
        var differences = StateDump.Compare(Dump(a), Dump(b));
        output.WriteLine($"desync at {tick} ({sections}), fault at {faultTick}:\n" + string.Join("\n", differences));
        Assert.Contains($"units#{victim}: only here", differences);
    }

    [Fact]
    public void A_pause_stops_both_machines_until_either_resumes()
    {
        var (a, b, toA, toB) = Pair();
        int frame = 0;
        Run(a, b, toA, toB, 10 * T, ref frame);
        a.Net.SetPaused(true);
        Run(a, b, toA, toB, 3 * T, ref frame);
        int pausedA = a.Sim.State.Tick, pausedB = b.Sim.State.Tick;
        Assert.True(b.Net.Paused);
        Assert.Equal(0, b.Net.PausedBy);
        Run(a, b, toA, toB, 3 * T, ref frame);
        Assert.Equal((pausedA, pausedB), (a.Sim.State.Tick, b.Sim.State.Tick));

        b.Net.SetPaused(false); // the other one resumes
        Run(a, b, toA, toB, 3 * T, ref frame);
        Assert.False(a.Net.Paused);
        Assert.True(a.Sim.State.Tick > pausedA + 2 * T);
        Assert.Null(a.Net.Desync);
    }

    [Fact]
    public void A_machine_speaks_only_for_its_own_player()
    {
        var net = new Lockstep(1, 2, Delay, 1, "main");
        net.Issue(new MoveCommand(0, 5, default));        // the other side's
        net.Issue(new DestroyCommand(5));                 // scripted
        net.Issue(new StopCommand(1, 7));                 // its own
        var sim = new Simulation();
        sim.AddPlayer(); sim.AddPlayer();
        net.Begin(sim);
        var turn = net.Outbox.Dequeue();
        using var r = NetMessage.Read(turn);
        Assert.Equal(Delay, r.ReadInt32());
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(new StopCommand(1, 7), CommandCodec.Read(r));
    }

    [Fact]
    public void Leaving_stops_the_other_machine()
    {
        var (a, b, toA, toB) = Pair();
        int frame = 0;
        Run(a, b, toA, toB, 5 * T, ref frame);
        a.Net.Leave();
        Run(a, b, toA, toB, T, ref frame);
        Assert.True(b.Net.PeerLeft);
        Assert.False(b.Net.Ready(b.Sim.State.Tick));
    }

    [Fact]
    public void Handshake_messages_round_trip()
    {
        var hello = new NetMessage.Hello(NetMessage.Protocol, "build-1", 0xDEADBEEF12345678);
        Assert.Equal(hello, NetMessage.ReadHello(NetMessage.WriteHello(hello)));
        var setup = new NetMessage.Setup(123456789, 1, 4);
        Assert.Equal(setup, NetMessage.ReadSetup(NetMessage.WriteSetup(setup)));
        Assert.Equal(NetMessage.RejectReason.Data, NetMessage.ReadReject(NetMessage.WriteReject(NetMessage.RejectReason.Data)));
        Assert.Equal(NetMessage.Kind.Accept, NetMessage.KindOf(NetMessage.Write(NetMessage.Kind.Accept)));
    }
}
