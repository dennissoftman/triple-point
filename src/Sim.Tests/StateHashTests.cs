using System.Collections;
using System.Numerics;
using System.Reflection;
using Sim.Ai;

namespace Sim.Tests;

/// <summary>
/// The state hash and the command codec are complete: change any field of anything in the state and the
/// hash changes; every Command type there is survives the trip through bytes. A field added later and
/// forgotten in Simulation.Hash, or a command forgotten in CommandCodec, fails here instead of turning
/// into a desync nobody can find.
/// </summary>
public class StateHashTests
{
    // A main-map match two minutes in, with one of everything the hash walks over.
    static Simulation MidMatch()
    {
        var sim = MainMap.Load();
        var ais = new[] { new Commander(sim, 0), new Commander(sim, 1) };
        var commands = new List<Command>();
        for (int t = 0; t < 2 * 60 * Simulation.TicksPerSecond; t++)
        {
            commands.Clear();
            foreach (var ai in ais) commands.AddRange(ai.Think());
            sim.Tick(commands);
        }
        var s = sim.State;
        if (s.Projectiles.Count == 0) s.Projectiles.Add(new Projectile { Id = 1, Against = Against.Full });
        if (s.Pickups.Count == 0) s.Pickups.Add(new Pickup { Id = 1 });
        if (sim.Vision(0).Ghosts.Count == 0) sim.Vision(0).Ghosts.Add(new Ghost { Id = 1 });
        // A unit with an order queued and a path, so those count too.
        var units = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s.Units);
        units[0].Pending ??= new Queue<Order>();
        if (units[0].Pending.Count == 0) units[0].Pending.Enqueue(new Order(UnitOrder.Move, Vector3.One));
        return sim;
    }

    // Every element list the hash walks: the list, and what it holds.
    static IEnumerable<(string Name, IList List)> Lists(Simulation sim)
    {
        var s = sim.State;
        yield return ("player", s.Players);
        yield return ("unit", s.Units);
        yield return ("building", s.Buildings);
        yield return ("depot", s.Gatherers);
        yield return ("projectile", s.Projectiles);
        yield return ("pickup", s.Pickups);
        yield return ("road", s.Belts);
        yield return ("road piece", s.Belts[0].Segments);
        yield return ("truck", s.Belts.First(l => l.Packages.Count > 0).Packages);
        yield return ("ghost", sim.Vision(0).Ghosts);
    }

    [Fact]
    public void Changing_any_field_of_anything_changes_the_hash()
    {
        var sim = MidMatch();
        var missed = new List<string>();
        int checkedFields = 0;
        foreach (var (name, list) in Lists(sim))
        {
            Assert.True(list.Count > 0, $"no {name} to check");
            var type = list[0]!.GetType();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var before = sim.Hash();
                object item = list[0]!;
                var old = field.GetValue(item);
                field.SetValue(item, Changed(field.FieldType, old, sim));
                if (type.IsValueType) list[0] = item; // a boxed copy: put it back
                if (sim.Hash() == before) missed.Add($"{name}.{field.Name}");
                field.SetValue(item, old);
                if (type.IsValueType) list[0] = item;
                Assert.Equal(before, sim.Hash());
                checkedFields++;
            }
        }
        Assert.True(missed.Count == 0, "the hash misses: " + string.Join(", ", missed));
        Assert.True(checkedFields > 120, $"{checkedFields} fields");
    }

    // Another value of the same type.
    static object? Changed(Type type, object? value, Simulation sim)
    {
        if (type == typeof(int)) return (int)value! + 1;
        if (type == typeof(uint)) return (uint)value! + 1;
        if (type == typeof(float)) return float.IsNaN((float)value!) ? 0f : (float)value! + 1;
        if (type == typeof(bool)) return !(bool)value!;
        if (type == typeof(Vector3)) return float.IsNaN(((Vector3)value!).X) ? Vector3.Zero : (Vector3)value! + Vector3.UnitX;
        if (type == typeof(string)) return (string?)value + "x";
        if (type == typeof(string[])) return ((string[]?)value ?? []).Append("x").ToArray();
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue((Array.IndexOf(values, value) + 1) % values.Length);
        }
        if (type == typeof(Against)) return value is Against a ? a with { Vehicle = a.Vehicle + 1 } : Against.Full;
        if (type == typeof(Order)) return (Order)value! with { TargetId = ((Order)value!).TargetId + 1 };
        if (type == typeof(Queue<Order>)) { var q = new Queue<Order>((Queue<Order>?)value ?? new()); q.Enqueue(default); return q; }
        if (type == typeof(BuildingType)) return value is BuildingType b ? b with { Id = b.Id + "x" } : sim.BuildingTypes.Values.First();
        if (type == typeof(BezierSegment)) { var c = (BezierSegment)value!; return new BezierSegment(c.P0 + Vector3.UnitX, c.P1, c.P2, c.P3); }
        if (type == typeof(List<UnitType>)) return ((List<UnitType>)value!).Append(new UnitType(1, 1, 1, Id: "x")).ToList();
        if (type == typeof(List<Package>)) return ((List<Package>)value!).Append(default).ToList();
        if (type == typeof(BeltSegment[])) return ((BeltSegment[])value!).Append(((BeltSegment[])value!)[0]).ToArray();
        throw new NotSupportedException($"No way to change a {type.Name} yet: add one here.");
    }

    [Fact]
    public void Every_command_type_survives_the_trip_through_bytes()
    {
        var types = typeof(Command).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Command)) && !t.IsAbstract).ToArray();
        Assert.True(types.Length >= 20);
        foreach (var type in types)
        {
            // Every parameter gets a value unlike its default, so a field read back in the wrong place shows.
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            int n = 3;
            var args = ctor.GetParameters().Select(p => p.ParameterType switch
            {
                var t when t == typeof(int) => (object)(n++ * 7),
                var t when t == typeof(float) => n++ * 1.5f + 0.1f,
                var t when t == typeof(bool) => true,
                var t when t == typeof(string) => $"type{n++}",
                var t when t == typeof(Vector3) => new Vector3(n++, -0.25f, n++ * 1e-3f),
                var t => throw new NotSupportedException($"{type.Name}: a {t.Name} parameter"),
            }).ToArray();
            var command = (Command)ctor.Invoke(args);
            if (command is BreakSegmentCommand or DestroyCommand) Assert.Equal(Player.None, command.Player);

            var bytes = new MemoryStream();
            using (var w = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true)) CommandCodec.Write(w, command);
            bytes.Position = 0;
            using var r = new BinaryReader(bytes);
            Assert.Equal(command, CommandCodec.Read(r));
            Assert.Equal(bytes.Length, bytes.Position);
        }
    }
}
