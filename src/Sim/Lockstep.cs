namespace Sim;

/// <summary>
/// Lockstep for one machine: every machine runs the whole sim, and only commands travel. A command
/// issued now runs Delay ticks later, on every machine at once: before running tick t, this machine
/// sends its turn for t + Delay (what was issued since its last turn, maybe nothing), and tick t runs only
/// once every player's turn for t is in (the first Delay ticks have none: they're empty for everyone).
/// Each turn sent at a tick that's a multiple of HashEvery carries the state's hash then; a peer's hash
/// that differs from this machine's for the same tick is a desync, and both stop. Transport-agnostic:
/// messages go out through Outbox and come in through Receive, in order (ENet's reliable channel).
/// </summary>
public sealed class Lockstep
{
    public readonly int LocalPlayer, Players, Delay, HashEvery;

    /// <summary>What ran here: every tick's commands as run, and the hashes. Saved after the match, or at a desync.</summary>
    public readonly Replay Replay;

    public readonly Queue<byte[]> Outbox = new();

    /// <summary>Paused by someone (PausedBy): no ticks run anywhere until either player resumes.</summary>
    public bool Paused { get; private set; }
    public int PausedBy { get; private set; } = Player.None;

    /// <summary>The first tick whose hashes differed and the sections that did, once a desync is found (here or by the peer).</summary>
    public (int Tick, string Sections)? Desync { get; private set; }

    /// <summary>The peer said goodbye (or the transport lost it: PeerGone).</summary>
    public bool PeerLeft { get; private set; }

    /// <summary>The peer's state dump at the desync, once it arrives.</summary>
    public string? PeerDump { get; private set; }

    readonly Dictionary<int, Command[]>[] _turns;   // per player: tick -> its commands for it
    readonly List<Command> _pending = [];          // issued here since the last turn sent
    readonly List<Command> _run = [];              // the tick's commands, merged
    readonly List<Command> _faults = [];           // InjectFault's
    readonly Dictionary<int, StateHash> _own = [];
    readonly Dictionary<int, StateHash>[] _theirs; // per player: tick -> its hash

    public Lockstep(int localPlayer, int players, int delay, uint seed, string setup, int hashEvery = Replay.DefaultHashEvery)
    {
        (LocalPlayer, Players, Delay, HashEvery) = (localPlayer, players, Math.Max(1, delay), hashEvery);
        _turns = [.. Enumerable.Range(0, players).Select(_ => new Dictionary<int, Command[]>())];
        _theirs = [.. Enumerable.Range(0, players).Select(_ => new Dictionary<int, StateHash>())];
        Replay = new Replay { Seed = seed, Setup = setup, HashEvery = hashEvery };
    }

    /// <summary>
    /// A command from this machine's player, for its next turn. Commands for anyone else (a scripted
    /// one, another player's units) are dropped: a machine only speaks for its own player.
    /// </summary>
    public void Issue(Command command)
    {
        if (command.Player == LocalPlayer) _pending.Add(command);
    }

    /// <summary>
    /// Whether tick `tick` can run: not paused, no desync, and every player's turn for it in. A peer that
    /// left sends no more turns, so the ticks it already sent still run, and then nothing does.
    /// </summary>
    public bool Ready(int tick)
    {
        if (Paused || Desync is not null) return false;
        if (tick < Delay) return true;
        foreach (var turns in _turns)
            if (!turns.ContainsKey(tick)) return false;
        return true;
    }

    /// <summary>
    /// Call with the sim about to run its next tick (Ready for it): sends this machine's turn for Delay
    /// ticks on, notes the hash if due, and returns the tick's commands, every player's in player order.
    /// The list is reused: valid until the next call.
    /// </summary>
    public IReadOnlyList<Command> Begin(Simulation sim)
    {
        int tick = sim.State.Tick;
        StateHash? hash = null;
        if (tick % HashEvery == 0)
        {
            hash = sim.Hash();
            _own[tick] = hash.Value;
            Replay.Hashes.Add(hash.Value);
            for (int p = 0; p < Players; p++) Compare(p, tick);
        }

        var mine = _pending.ToArray();
        _pending.Clear();
        _turns[LocalPlayer][tick + Delay] = mine;
        Outbox.Enqueue(NetMessage.Write(NetMessage.Kind.Turn, w =>
        {
            w.Write(tick + Delay); w.Write((byte)LocalPlayer); w.Write(mine.Length);
            foreach (var c in mine) CommandCodec.Write(w, c);
            w.Write(hash is not null);
            if (hash is not null) NetMessage.WriteHash(w, hash.Value);
        }));

        _run.Clear();
        if (tick >= Delay)
            for (int p = 0; p < Players; p++)
            {
                _run.AddRange(_turns[p][tick]);
                _turns[p].Remove(tick);
            }
        _run.AddRange(_faults);
        _faults.Clear();
        Replay.Record(tick, _run);
        return _run;
    }

    /// <summary>
    /// For testing the desync path: a command that runs here at the next tick but is never sent, as if
    /// this machine computed something the other didn't. It's in this machine's replay, so playing that
    /// back reproduces this machine's state.
    /// </summary>
    public void InjectFault(Command command) => _faults.Add(command);

    public void Receive(byte[] message)
    {
        switch (NetMessage.KindOf(message))
        {
            case NetMessage.Kind.Turn:
            {
                using var r = NetMessage.Read(message);
                int tick = r.ReadInt32(), player = r.ReadByte();
                var commands = new Command[r.ReadInt32()];
                for (int i = 0; i < commands.Length; i++) commands[i] = CommandCodec.Read(r);
                if (player < 0 || player >= Players || player == LocalPlayer) break;
                _turns[player][tick] = commands;
                if (r.ReadBoolean())
                {
                    var hash = NetMessage.ReadHash(r);
                    _theirs[player][hash.Tick] = hash;
                    Compare(player, hash.Tick);
                }
                break;
            }
            case NetMessage.Kind.Pause:
            {
                using var r = NetMessage.Read(message);
                (Paused, PausedBy) = (r.ReadBoolean(), r.ReadByte());
                if (!Paused) PausedBy = Player.None;
                break;
            }
            case NetMessage.Kind.Desync:
            {
                using var r = NetMessage.Read(message);
                (int tick, string sections) = (r.ReadInt32(), r.ReadString());
                Desync ??= (tick, sections);
                break;
            }
            case NetMessage.Kind.Dump:
            {
                using var r = NetMessage.Read(message);
                PeerDump = r.ReadString();
                break;
            }
            case NetMessage.Kind.Bye: PeerLeft = true; break;
        }
    }

    /// <summary>Pauses or resumes, for everyone.</summary>
    public void SetPaused(bool paused)
    {
        (Paused, PausedBy) = (paused, paused ? LocalPlayer : Player.None);
        Outbox.Enqueue(NetMessage.Write(NetMessage.Kind.Pause, w => { w.Write(paused); w.Write((byte)LocalPlayer); }));
    }

    /// <summary>Leaving: tells the peer.</summary>
    public void Leave() => Outbox.Enqueue(NetMessage.Write(NetMessage.Kind.Bye));

    /// <summary>The transport lost the peer.</summary>
    public void PeerGone() => PeerLeft = true;

    /// <summary>Sends this machine's state dump at the desync, for the peer to compare with its own.</summary>
    public void SendDump(string dump) => Outbox.Enqueue(NetMessage.Write(NetMessage.Kind.Dump, w => w.Write(dump)));

    // Both hashes for a tick in: they match, or that's the desync (the first one found stands).
    void Compare(int player, int tick)
    {
        if (player == LocalPlayer || !_own.TryGetValue(tick, out var mine) || !_theirs[player].TryGetValue(tick, out var theirs)) return;
        _theirs[player].Remove(tick);
        if (mine == theirs) return;
        if (Desync is not null) return;
        Desync = (tick, mine.Differences(theirs));
        Outbox.Enqueue(NetMessage.Write(NetMessage.Kind.Desync, w => { w.Write(tick); w.Write(Desync.Value.Sections); }));
    }
}
