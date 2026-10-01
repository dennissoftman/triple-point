using Sim;
using Sim.Ai;
using Sim.Runner;

// Plays matches headless on the main map, for determinism checks (DeterminismTests runs it under
// different JIT settings and compares the output) and for replays.
//
//   Sim.Runner [--seed=N] [--minutes=M] [--every=T] [--level=easy|normal] [--record=<file>]
//       the commander AI against itself: prints "<tick> <hash>" every T ticks (20), then each section's
//       hash at the end; --sections prints every section on every line; --record saves the match as a replay.
//   Sim.Runner --replay=<file>
//       the replay's commands alone (no AI) on a fresh main map, checking every hash it recorded.

string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..];

if (Arg("replay") is string replayPath)
{
    Replay replay;
    using (var file = File.OpenRead(replayPath)) replay = Replay.Load(file);
    var sim = MainMap.Load(replay.Seed);
    var result = replay.Verify(sim);
    Console.WriteLine(result is var (tick, sections) ? $"desync at tick {tick}: {sections}" : $"ok: {replay.Hashes.Count} hashes, to tick {sim.State.Tick}");
    return result is null ? 0 : 1;
}

uint seed = uint.Parse(Arg("seed") ?? "1");
int ticks = (int)(float.Parse(Arg("minutes") ?? "5", System.Globalization.CultureInfo.InvariantCulture) * 60 * Simulation.TicksPerSecond);
int every = int.Parse(Arg("every") ?? "20");
var level = Arg("level") == "easy" ? AiLevel.Easy : AiLevel.Normal;

var match = MainMap.Load(seed);
var ais = new[] { new Commander(match, 0, level), new Commander(match, 1, level) };
var recording = Arg("record") is string recordPath ? new Replay { Setup = "main", Seed = seed } : null;
var commands = new List<Command>();
bool sectionsEach = args.Contains("--sections");
string Sections(StateHash h) => string.Join(" ", StateHash.Sections.Select((name, i) => $"{name}={h.Section(i):x16}"));
if (sectionsEach) Console.WriteLine($"0 {Sections(match.Hash())}");
recording?.RecordHash(match);
while (match.State.Tick < ticks && !match.State.GameOver)
{
    commands.Clear();
    foreach (var ai in ais) commands.AddRange(ai.Think());
    recording?.Record(match.State.Tick, commands);
    match.Tick(commands);
    recording?.RecordHash(match);
    if (match.State.Tick % every == 0) Console.WriteLine($"{match.State.Tick} " + (sectionsEach ? Sections(match.Hash()) : $"{match.Hash().All:x16}"));
}
var last = match.Hash();
Console.WriteLine($"end {last.Tick} {Sections(last)}");
if (recording is not null)
{
    if (last.Tick % recording.HashEvery != 0) recording.Hashes.Add(last); // the end, wherever it fell
    using var file = File.Create(Arg("record")!);
    recording.Save(file);
}
return 0;
