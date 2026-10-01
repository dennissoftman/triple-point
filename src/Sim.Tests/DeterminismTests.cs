using System.Diagnostics;
using System.Text.RegularExpressions;
using Sim.Ai;
using Xunit.Abstractions;

namespace Sim.Tests;

/// <summary>
/// Lockstep needs every machine to compute the same bits from the same commands. These hold the sim to
/// it: the same match twice in one process, replays that check themselves, the views' queries leaving the
/// state alone, the math that differs between CPUs kept out of the source, and the same match under
/// every JIT and instruction-set setting that could differ between players' machines.
/// </summary>
public class DeterminismTests(ITestOutputHelper output)
{
    const int T = Simulation.TicksPerSecond;

    static (Simulation Sim, Commander[] Ais) AiMatch(uint seed = 1) { var sim = MainMap.Load(seed); return (sim, [new Commander(sim, 0), new Commander(sim, 1)]); }

    static void Step(Simulation sim, Commander[] ais, List<Command> commands)
    {
        commands.Clear();
        foreach (var ai in ais) commands.AddRange(ai.Think());
        sim.Tick(commands);
    }

    [Fact]
    public void The_same_match_twice_hashes_the_same_every_second()
    {
        var (a, aiA) = AiMatch();
        var (b, aiB) = AiMatch();
        var (ca, cb) = (new List<Command>(), new List<Command>());
        Assert.Equal(a.Hash(), b.Hash());
        for (int t = 1; t <= 3 * 60 * T; t++)
        {
            Step(a, aiA, ca);
            Step(b, aiB, cb);
            if (t % T == 0) Assert.True(a.Hash() == b.Hash(), $"tick {t}: {a.Hash().Differences(b.Hash())}");
        }
    }

    [Fact]
    public void Another_seed_is_another_match()
    {
        var (a, aiA) = AiMatch(1);
        var (b, aiB) = AiMatch(2);
        var commands = new List<Command>();
        for (int t = 0; t < 90 * T; t++) { Step(a, aiA, commands); Step(b, aiB, commands); }
        Assert.NotEqual(a.Hash().All, b.Hash().All);
    }

    [Fact]
    public void A_replay_of_the_commands_alone_gives_the_same_hashes()
    {
        var (sim, ais) = AiMatch(3);
        var replay = new Replay { Setup = "main", Seed = 3 };
        var commands = new List<Command>();
        replay.RecordHash(sim);
        for (int t = 0; t < 3 * 60 * T; t++)
        {
            commands.Clear();
            foreach (var ai in ais) commands.AddRange(ai.Think());
            replay.Record(sim.State.Tick, commands);
            sim.Tick(commands);
            replay.RecordHash(sim);
        }
        Assert.True(replay.Turns.Count > 20, "the AIs issued commands");

        var bytes = new MemoryStream();
        replay.Save(bytes);
        bytes.Position = 0;
        var loaded = Replay.Load(bytes);
        Assert.Equal(replay.Hashes, loaded.Hashes);
        Assert.Null(loaded.Verify(MainMap.Load(3))); // without the AIs: commands are all they did

        // One command changed and the replay notices, from that tick on.
        int i = loaded.Turns.FindIndex(turn => turn.Commands.Any(c => c is MoveCommand or AttackMoveCommand));
        var (tick, changed) = loaded.Turns[i];
        int j = Array.FindIndex(changed, c => c is MoveCommand or AttackMoveCommand);
        changed[j] = changed[j] switch
        {
            MoveCommand m => m with { Target = m.Target + new System.Numerics.Vector3(5, 0, 0) },
            AttackMoveCommand m => m with { Target = m.Target + new System.Numerics.Vector3(5, 0, 0) },
            var c => c,
        };
        var desync = loaded.Verify(MainMap.Load(3));
        Assert.NotNull(desync);
        Assert.True(desync.Value.Tick > tick, $"found at {desync.Value.Tick}, changed at {tick}");
        Assert.Contains("units", desync.Value.Sections);
        output.WriteLine($"changed a command at tick {tick}; the replay caught it at tick {desync.Value.Tick} ({desync.Value.Sections})");
    }

    [Fact]
    public void The_views_queries_leave_the_state_alone()
    {
        // Each machine's views ask about their own side only, so a query that changed anything would split them.
        var (sim, ais) = AiMatch();
        var commands = new List<Command>();
        for (int t = 0; t < 2 * 60 * T; t++) Step(sim, ais, commands);
        var before = sim.Hash();
        var s = sim.State;
        var post = sim.BuildingTypes.Values.First(b => b.Kind == BuildingKind.Post);
        var barracks = sim.BuildingTypes["barracks"];
        var visibility = new float[sim.VisionWidth * sim.VisionDepth];
        var spots = new List<(int, float, float)>();
        for (int p = 0; p < 2; p++)
        {
            foreach (var u in s.Units)
            {
                sim.PathOf(u, out _);
                sim.OrderPoint(u.Current, u.Position);
                sim.SeesUnit(p, u);
                sim.Sees(p, u.Position);
                sim.SeesArea(p, u.Position, 3);
                sim.TryGetTarget(u.Id, out _, out _);
                sim.FindSegment(u.Position, 6, out _, out _);
                sim.SnapPost(u.Position, 10, out _, out _);
                sim.ShareAt(u.Position, out _, out _);
                sim.TooCloseToPost(u.Position);
                sim.CanPlace(barracks, u.Position);
                sim.CanPlace(post, u.Position);
                sim.OnPavedRoad(u.Position);
            }
            foreach (var line in s.Belts)
                foreach (var truck in line.Packages) sim.FindTruck(truck.Id, out _);
            for (int l = 0; l < s.Belts.Count; l++)
                for (int g = 0; g < s.Belts[l].Segments.Length; g++) { sim.SeenState(p, l, g); sim.SeenHealth(p, l, g); sim.FeedsPostOf(p, l, g); }
            sim.HasRequired(p, barracks);
            sim.CanAfford(p, barracks);
            sim.PostSpots(spots);
            sim.CopyVisibility(p, visibility);
            sim.Vision(p);
        }
        Assert.True(before == sim.Hash(), before.Differences(sim.Hash()));
    }

    [Fact]
    public void SimMath_is_as_close_to_MathF_as_floats_go()
    {
        var random = new SimRandom(9);
        for (int i = 0; i < 200_000; i++)
        {
            float x = random.Range(-1000, 1000), y = random.Range(-1000, 1000);
            Assert.True(MathF.Abs(SimMath.Sin(x) - MathF.Sin(x)) < 2e-6f, $"sin {x}");
            Assert.True(MathF.Abs(SimMath.Cos(x) - MathF.Cos(x)) < 2e-6f, $"cos {x}");
            Assert.True(MathF.Abs(SimMath.Atan2(y, x) - MathF.Atan2(y, x)) < 1e-6f, $"atan2 {y} {x}");
            float small = random.Range(-4, 4);
            Assert.True(MathF.Abs(SimMath.Sin(small) - MathF.Sin(small)) < 2e-7f, $"sin {small}");
        }
        foreach (var (y, x) in new[] { (0f, 0f), (0f, -0f), (-0f, -1f), (0f, -1f), (1f, 0f), (-1f, 0f), (0f, 1f), (3f, -3f), (-2f, -2f) })
            Assert.Equal(MathF.Atan2(y, x), SimMath.Atan2(y, x), 6);
        Assert.Equal(0f, SimMath.Sin(0));
        Assert.Equal(1f, SimMath.Cos(0));
    }

    // Each a call that gives different bits on different machines (the C runtime's transcendentals; the
    // fused multiply-add System.Numerics uses on some CPUs), or a source of anything but the seed.
    static readonly (string Pattern, string Why)[] Banned =
    [
        (@"\bMathF?\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Sinh|Cosh|Tanh|Exp|Log|Log2|Log10|Pow|Cbrt|SinCos)\(", "the C runtime's; use SimMath"),
        (@"\b(Vector[234]|Quaternion|Matrix[34]x[34]|float|double|MathF?)\.(Lerp|Slerp|Reflect|FusedMultiplyAdd|MultiplyAddEstimate)\(", "fused on some CPUs; use SimMath.Lerp"),
        (@"\bnew Random\b|\bRandom\.Shared\b|\bSystem\.Random\b", "the sim's randomness is SimRandom, from the seed"),
        (@"\bDateTime\b|\bStopwatch\b|\bEnvironment\.TickCount|\bTimeProvider\b", "time differs between machines"),
        (@"\bParallel\.|\bTask\.Run\b|\bThreadPool\b", "threads finish in any order"),
        (@"\.GetHashCode\(\)|\bHashCode\.Combine\b", "string hashes are randomized per process"),
    ];

    [Fact]
    public void The_sim_uses_none_of_what_differs_between_machines()
    {
        var dir = Path.Combine(Repo(), "src", "Sim");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Split("//")[0]; // comments may name them
                foreach (var (pattern, why) in Banned)
                    if (Regex.IsMatch(code, pattern)) found.Add($"{Path.GetFileName(file)}:{i + 1}: {code.Trim()} ({why})");
            }
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    /// <summary>
    /// The same 5-minute AI match in separate processes, built Debug and Release, under every JIT setting
    /// that could differ between players' machines: no tiering, no optimization, no SIMD, no AVX or AVX2
    /// (older CPUs), no precompiled framework code. One differing bit anywhere shows in the end hashes.
    /// </summary>
    [Fact]
    public void The_same_match_hashes_the_same_under_every_JIT_and_CPU_setting()
    {
        var repo = Repo();
        var project = Path.Combine(repo, "src", "Sim.Runner", "Sim.Runner.csproj");
        var release = Path.Combine(repo, "src", "Sim.Runner", "bin", "Release", "net10.0", "Sim.Runner.dll");
        var build = Run("dotnet", $"build \"{project}\" -c Release -nologo -v q", []);
        Assert.True(build.Code == 0, build.Output);
        var debug = Path.Combine(AppContext.BaseDirectory, "Sim.Runner.dll");

        (string Name, string Dll, (string, string)[] Env)[] runs =
        [
            ("debug", debug, []),
            ("release", release, []),
            ("no tiering", release, [("DOTNET_TieredCompilation", "0")]),
            ("min opts", release, [("DOTNET_JITMinOpts", "1")]),
            ("no PGO", release, [("DOTNET_TieredPGO", "0")]),
            ("no SIMD", release, [("DOTNET_EnableHWIntrinsic", "0")]),
            ("no AVX2", release, [("DOTNET_EnableAVX2", "0")]),
            ("no AVX", release, [("DOTNET_EnableAVX", "0")]),
            ("no AVX-512", release, [("DOTNET_EnableAVX512F", "0")]),
            ("no ReadyToRun", release, [("DOTNET_ReadyToRun", "0")]),
        ];
        var results = runs.AsParallel().Select(r => (r.Name, Result: Run("dotnet", $"\"{r.Dll}\" --minutes=5 --every=1200", r.Env))).ToArray();
        foreach (var (name, result) in results)
        {
            Assert.True(result.Code == 0, $"{name}: {result.Output}");
            output.WriteLine($"{name,-14} {result.Output.Trim().Split('\n')[^1].Trim()[..60]}");
        }
        var expected = results[0].Result.Output;
        foreach (var (name, result) in results)
            Assert.True(result.Output == expected, $"{name} differs from debug:\n{result.Output}\nvs\n{expected}");
    }

    static (int Code, string Output) Run(string file, string arguments, (string, string)[] env)
    {
        var start = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (key, value) in env) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("No Game.sln above the test binaries.");
    }
}
