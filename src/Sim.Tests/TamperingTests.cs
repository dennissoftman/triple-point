using System.Numerics;

namespace Sim.Tests;

/// <summary>
/// A networked match can face a modified game on the other machine. What it sends is checked (Lockstep):
/// a forged or broken message stops the match as a violation, never crashes it or slips through. What the
/// sim is told to do is checked the same way on both machines: junk commands do nothing and break nothing.
/// What it can't stop: seeing through the fog (every machine holds the whole state; doctrine: Multiplayer).
/// </summary>
public class TamperingTests
{
    const int Delay = 3;

    // A turn as the peer (player 1) would send it, for player 0's machine.
    static byte[] Turn(int tick, Command[] commands, StateHash? hash = null, int player = 1, Action<BinaryWriter>? extra = null) =>
        NetMessage.Write(NetMessage.Kind.Turn, w =>
        {
            w.Write(tick); w.Write((byte)player); w.Write(commands.Length);
            foreach (var c in commands) CommandCodec.Write(w, c);
            w.Write(hash is not null);
            if (hash is { } h) NetMessage.WriteHash(w, h);
            extra?.Invoke(w);
        });

    static StateHash HashAt(int tick) => new(tick, 1, 2, 3, 4, 5, 6, 7, 8, 9);

    // Player 0's machine with the peer's first few honest turns in.
    static Lockstep Machine()
    {
        var net = new Lockstep(0, 2, Delay, 1, "main");
        net.Receive(Turn(Delay, [], HashAt(0))); // tick 3 carries the hash of tick 0
        net.Receive(Turn(Delay + 1, []));
        Assert.Null(net.Violation);
        return net;
    }

    [Fact]
    public void Orders_for_the_other_sides_units_stop_the_match()
    {
        var net = Machine();
        net.Receive(Turn(Delay + 2, [new StopCommand(1, 7), new MoveCommand(0, 12, Vector3.One)]));
        Assert.Contains("MoveCommand for player 0", net.Violation);
        Assert.False(net.Ready(Delay + 2));
    }

    [Fact]
    public void A_scripted_command_from_a_player_stops_the_match()
    {
        var net = Machine();
        net.Receive(Turn(Delay + 2, [new DestroyCommand(12)])); // Player.None's: no machine may send it
        Assert.Contains("DestroyCommand", net.Violation);
    }

    [Fact]
    public void Skipped_repeated_and_early_turns_stop_the_match()
    {
        var skipped = Machine();
        skipped.Receive(Turn(Delay + 5, []));
        Assert.Contains("when 5 was next", skipped.Violation);

        var repeated = Machine();
        repeated.Receive(Turn(Delay + 1, []));
        Assert.NotNull(repeated.Violation);

        var mine = Machine();
        mine.Receive(Turn(Delay + 2, [], player: 0)); // claiming to be this machine's player
        Assert.Contains("player 0", mine.Violation);
    }

    [Fact]
    public void A_turn_without_its_hash_stops_the_match()
    {
        // Leaving the hash out would hide a changed state from the check.
        var net = Machine();
        for (int t = Delay + 2; t < Replay.DefaultHashEvery + Delay; t++) net.Receive(Turn(t, []));
        Assert.Null(net.Violation);
        net.Receive(Turn(Replay.DefaultHashEvery + Delay, []));
        Assert.Contains("without its hash", net.Violation);

        var early = Machine();
        early.Receive(Turn(Delay + 2, [], HashAt(Delay + 2 - Delay)));
        Assert.Contains("hash out of turn", early.Violation);
    }

    [Fact]
    public void Broken_and_oversized_messages_stop_the_match_without_a_crash()
    {
        var truncated = Machine();
        var whole = Turn(Delay + 2, [new StopCommand(1, 7)]);
        truncated.Receive(whole[..^3]);
        Assert.Contains("malformed", truncated.Violation);

        var trailing = Machine();
        trailing.Receive(Turn(Delay + 2, [], extra: w => w.Write(42)));
        Assert.Contains("malformed", trailing.Violation);

        var junk = Machine();
        var random = new SimRandom(3);
        var bytes = new byte[200];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)random.NextUInt();
        bytes[0] = (byte)NetMessage.Kind.Turn;
        junk.Receive(bytes);
        Assert.NotNull(junk.Violation);

        var flood = Machine();
        flood.Receive(Turn(Delay + 2, [.. Enumerable.Repeat<Command>(new StopCommand(1, 7), Lockstep.MaxCommandsPerTurn + 1)]));
        Assert.Contains("commands in one turn", flood.Violation);

        var empty = Machine();
        empty.Receive([]);
        Assert.NotNull(empty.Violation);

        var handshake = Machine(); // no lobby messages once playing
        handshake.Receive(NetMessage.WriteSetup(new NetMessage.Setup(1, 0, 3)));
        Assert.NotNull(handshake.Violation);
    }

    [Fact]
    public void Honest_turns_pass()
    {
        var net = Machine();
        for (int t = Delay + 2; t < 200; t++)
            net.Receive(Turn(t, [new MoveCommand(1, 5, new Vector3(t, 0, 0)), new ProduceCommand(1, 9, "rifle_squad", 5)],
                (t - Delay) % Replay.DefaultHashEvery == 0 ? HashAt(t - Delay) : null));
        Assert.Null(net.Violation);
    }

    /// <summary>
    /// Thousands of random commands, most of them nonsense (other players' and unknown ids, NaN, infinity,
    /// points far off the map, negative counts, unknown types), on the real map: nothing throws, and two
    /// sims given the same junk stay the same.
    /// </summary>
    [Fact]
    public void Junk_commands_break_nothing_and_split_nothing()
    {
        var a = MainMap.Load(4);
        var b = MainMap.Load(4);
        var random = new SimRandom(11);
        float[] floats = [0, 1, -1, 7.5f, 1e9f, -1e30f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, -0f];
        string[] names = ["rifle_squad", "tank", "builder", "barracks", "post", "hq", "", "nonsense", "turret"];
        int Pick(int n) => (int)(random.NextUInt() % (uint)n);
        float F() => Pick(3) == 0 ? floats[Pick(floats.Length)] : random.Range(-150, 150);
        Vector3 V() => new(F(), Pick(4) == 0 ? F() : 0, F());
        int player = 0;
        int Id(Simulation sim)
        {
            var s = sim.State;
            if (Pick(2) == 0) // its own, half the time: those it may order
            {
                var own = s.Units.Where(u => u.Owner == player).Select(u => u.Id).Concat(s.Buildings.Where(b => b.Owner == player).Select(b => b.Id)).ToArray();
                if (own.Length > 0) return own[Pick(own.Length)];
            }
            return Pick(5) switch
            {
                0 when s.Units.Count > 0 => s.Units[Pick(s.Units.Count)].Id,
                1 when s.Buildings.Count > 0 => s.Buildings[Pick(s.Buildings.Count)].Id,
                2 when s.Gatherers.Count > 0 => s.Gatherers[Pick(s.Gatherers.Count)].Id,
                3 => Pick(2) == 0 ? -1 : int.MaxValue,
                _ => Pick(500),
            };
        }
        int Small() => Pick(4) == 0 ? (Pick(2) == 0 ? -5 : int.MaxValue) : Pick(30);

        var commands = new List<Command>();
        for (int tick = 0; tick < 3 * 60 * Simulation.TicksPerSecond; tick++)
        {
            commands.Clear();
            for (int n = Pick(12); n > 0; n--)
            {
                int p = player = Pick(2);
                bool q = Pick(2) == 0;
                commands.Add(Pick(18) switch
                {
                    0 => new MoveCommand(p, Id(a), V(), q),
                    1 => new AttackCommand(p, Id(a), Id(a), q),
                    2 => new AttackMoveCommand(p, Id(a), V(), q),
                    3 => new AttackSegmentCommand(p, Id(a), Small(), Small(), q),
                    4 => new MendCommand(p, Id(a), Id(a), q),
                    5 => new GarrisonCommand(p, Id(a), Id(a), q),
                    6 => new ExitCommand(p, Id(a), Pick(2) == 0 ? -1 : Id(a)),
                    7 => new RepairCommand(p, Id(a), Id(a), q),
                    8 => new SetRetreatCommand(p, Id(a), q),
                    9 => new RepairSegmentCommand(p, Id(a), Small(), Small(), q),
                    10 => new PaveSegmentCommand(p, Id(a), Small(), Small(), q),
                    11 => new StopCommand(p, Id(a)),
                    12 => new HoldCommand(p, Id(a)),
                    13 => new ProduceCommand(p, Id(a), names[Pick(names.Length)], Pick(3) == 0 ? -3 : Small()),
                    14 => new CancelProductionCommand(p, Id(a), Small()),
                    15 => new SetRallyCommand(p, Id(a), V()),
                    16 => new BuildCommand(p, Id(a), names[Pick(names.Length)], V(), F(), q),
                    _ => new ResumeBuildCommand(p, Id(a), Id(a), q),
                });
            }
            a.Tick(commands);
            b.Tick(commands);
            if (tick % 200 == 0) Assert.True(a.Hash() == b.Hash(), $"tick {tick}: {a.Hash().Differences(b.Hash())}");
            foreach (var u in a.State.Units)
            {
                Assert.True(float.IsFinite(u.Position.X) && float.IsFinite(u.Position.Z) && float.IsFinite(u.Current.Target.X), $"tick {tick}: unit {u.Id} at {u.Position}, going to {u.Current.Target}");
                Assert.True(u.Pending.Count <= Simulation.MaxPending);
            }
            foreach (var building in a.State.Buildings) Assert.True(float.IsFinite(building.Rally.X), $"tick {tick}: rally {building.Rally}");
        }
    }
}
