namespace Sim;

/// <summary>
/// A match as its commands: what was issued on which tick, plus the state's hash every HashEvery ticks
/// (and at tick 0), so playing it back checks itself: the same commands on the same start must give the
/// same hashes, on any machine. Setup is what the match started from (the map, the seed, the settings),
/// for whoever plays it back to rebuild; the tick-0 hash says whether they got the same start. Lockstep
/// records one every match; a desync leaves both sides' replays to compare.
/// </summary>
public sealed class Replay
{
    const uint Magic = 0x50525054; // "TPRP"
    const int Version = 1;
    public const int DefaultHashEvery = 20;

    public string Setup = "";
    public uint Seed;
    public int HashEvery = DefaultHashEvery;
    public readonly List<(int Tick, Command[] Commands)> Turns = []; // only ticks with commands, in order
    public readonly List<StateHash> Hashes = [];                     // in order of tick

    /// <summary>Notes a tick's commands, if any (before running it).</summary>
    public void Record(int tick, IReadOnlyList<Command> commands)
    {
        if (commands.Count > 0) Turns.Add((tick, [.. commands]));
    }

    /// <summary>Notes the state's hash if it's due (after a tick, or at the start).</summary>
    public void RecordHash(Simulation sim)
    {
        if (sim.State.Tick % HashEvery == 0) Hashes.Add(sim.Hash());
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic); w.Write(Version); w.Write(Setup); w.Write(Seed); w.Write(HashEvery);
        w.Write(Turns.Count);
        foreach (var (tick, commands) in Turns)
        {
            w.Write(tick); w.Write(commands.Length);
            foreach (var c in commands) CommandCodec.Write(w, c);
        }
        w.Write(Hashes.Count);
        foreach (var h in Hashes)
        {
            w.Write(h.Tick);
            for (int i = 0; i < StateHash.Sections.Length; i++) w.Write(h.Section(i));
        }
    }

    public static Replay Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a replay.");
        if (r.ReadInt32() is int version && version != Version) throw new InvalidDataException($"Replay version {version}; this build reads {Version}.");
        var replay = new Replay { Setup = r.ReadString(), Seed = r.ReadUInt32(), HashEvery = r.ReadInt32() };
        for (int n = r.ReadInt32(); n > 0; n--)
        {
            int tick = r.ReadInt32();
            var commands = new Command[r.ReadInt32()];
            for (int i = 0; i < commands.Length; i++) commands[i] = CommandCodec.Read(r);
            replay.Turns.Add((tick, commands));
        }
        for (int n = r.ReadInt32(); n > 0; n--)
        {
            int tick = r.ReadInt32();
            var s = new ulong[StateHash.Sections.Length];
            for (int i = 0; i < s.Length; i++) s[i] = r.ReadUInt64();
            replay.Hashes.Add(new StateHash(tick, s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8]));
        }
        return replay;
    }

    /// <summary>
    /// Plays the commands on `sim` (built from Setup, at tick 0) up to the last recorded hash, checking
    /// each: null if every one matched, else the first mismatch (`sections` names what differs).
    /// </summary>
    public (int Tick, string Sections)? Verify(Simulation sim)
    {
        int turn = 0, hash = 0;
        var none = Array.Empty<Command>();
        while (hash < Hashes.Count)
        {
            var want = Hashes[hash];
            while (sim.State.Tick < want.Tick)
            {
                int tick = sim.State.Tick;
                var commands = turn < Turns.Count && Turns[turn].Tick == tick ? Turns[turn++].Commands : none;
                sim.Tick(commands);
            }
            var got = sim.Hash();
            if (got != want) return (want.Tick, want.Differences(got));
            hash++;
        }
        return null;
    }
}
