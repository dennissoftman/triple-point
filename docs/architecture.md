# Architecture

How the code is built, and the technical plans that aren't code yet. The invariants every change must keep are in `CLAUDE.md`; this document explains them and maps the code. Update it when a structure changes. Design rules are in `docs/doctrine.md`.

## Stack

- Godot 4.7.2 .NET, C# on .NET 10. Every project targets `net10.0`. Desktop C# runs on CoreCLR with a JIT (tiered compilation, dynamic PGO). There is no web export.
- `Game.sln` at the root is the solution Godot uses (`dotnet/project/solution_directory = res://..`). `godot/Game.csproj` references `src/Sim/Sim.csproj`.
- Code names stay neutral (`Game`, `Sim`) so a rename of the game touches nothing.

## Sim and view

`src/Sim` is a plain class library with no Godot reference. It owns all game state and rules. The Godot project is a host and a set of views over it.

- **One tick:** `Simulation.Tick(commands) -> events` at a fixed 20 Hz, advancing `SimState` in place. The returned event list is reused and valid until the next call.
- **Commands** (`Commands.cs`) are records carrying their issuing player: move, attack, attack-move, attack segment, repair segment, set junction, break segment (scripted), build and resume building (builders), and for buildings produce, cancel production, set repeat, set rally. Player input and, later, the AI issue the same commands. The sim drops commands to units and buildings the issuer doesn't own.
- **Events** (`SimEvent`) say what happened, for logs and effects: unit died, arrived or produced, package lost or gathered, gatherer or building destroyed, building placed or completed, build blocked, segment broken or repaired, junction captured or switched, shell hit.
- **Tick order:** apply commands, then update units (orders, movement, turrets, shots), move shells, remove the dead, capture switches, move packages, transfer at junctions, gatherers, construction, production, spawn packages, pickups. Builders only mark the foundation they work on during the unit update; construction grows it later in the tick, so a finished defense can add a unit without changing the unit list under the unit loop.
- **State** (`SimState.cs`): `Unit` and `Gatherer` are structs in lists, updated through spans. Belts, segments, junctions and buildings are classes. Packages are ordered front to back per line. Pickups and projectiles are unordered, removed by swapping with the last. Ids come from one counter per sim.
- **Randomness:** one seeded `SimRandom` (xorshift). Math goes through `System.Numerics`, and conversion to Godot types happens only in `SimConvert`. Determinism isn't required yet, but nothing rules it out: fixed-point math could replace floats later for lockstep.
- **Performance:** no allocations or LINQ inside the tick. Target search is all-pairs for now; a spatial grid comes with navigation.

## Where things are

| Concern | Sim (`src/Sim`) | View and host (`godot/scripts`) |
|---|---|---|
| Units, orders, movement, easing, turrets, weapons, shells, splash, return fire | `Simulation.cs` (units section), `SimState.cs` | `UnitsView` (instances, order paths of the selection, tracers, shells, flashes), `UnitView` (one unit's look, suspension lean), `UnitMaterials`, `HealthBar` |
| Belts, spill, pickups, junctions, capture, gatherers | `Simulation.cs`, `BezierSegment.cs` | `BeltView` (ribbons, packages, discs, arrows, posts), `CaptureRing`, `InstanceBatch` |
| Buildings, production queue, rally points | `Simulation.cs` (`UpdateProduction`), `SimState.cs` (`Building`) | `BuildingsView` (blocks, bars, rally flag), `CommandCard` |
| Construction: builders, foundations, placement, grid snap | `Simulation.cs` (`Construct`, `UpdateConstruction`, `CanPlace`, `SnapToGrid`) | `PlayerInput` (placement), `BuildingsView` (foundations, ghost), `CommandCard` |
| Minimap | | `Minimap` (canvas drawing; click, drag, right-click) |
| Game data | `GameData.cs` (parses `data/units.json`, `data/weapons.json`, `data/buildings.json`) | `SimHost.LoadData` reads the files |
| Host: ticks, game speed, map building, HUD, perf readout, demo script | | `SimHost` |
| Input, selection (units, or one building), placement, cursor, hotseat | | `PlayerInput`, `Cursors` |
| Camera | | `RtsCamera` (`CameraView`, `FlyTo`) |
| Map markers | | `OwnedMarker`, `UnitSpawn`, `BuildingSpawn`; `StressMap` generates the stress scene |

- **The host:** `SimHost._Process` adds real time multiplied by the game speed to an accumulator, runs the ticks that are due, logs events, and hands them to `UnitsView.OnEvent`. Then it syncs the views with `alpha`, the fraction of the way to the next tick, so movement interpolates smoothly.
- **Views read, never write:** views read `SimState` and never change it, and no game logic runs in per-node `_Process`. Units carry `Prev*` fields (position, heading, turret) for interpolation.
- **UI state stays on the Godot side:** selection, colors, names and the local player live in the views and input code; the sim only sees commands.
- **Input:** Input Map actions only, never literal keys. What a click would do is one `Intent`, computed once and used for both the cursor and the command, so the two can't disagree. `PlayerInput` tracks the mouse from its own events (the OS cursor position is wrong for injected events).
- **Godot C# conventions:** script classes are `partial` and named after their file. Prefer `[Export]` fields over `GetNode("path")`. Commit the `.uid` files.

## Game data

JSON in `/data`, parsed by `Sim` with `System.Text.Json`: comments, trailing commas, case-insensitive names, enums as strings. Godot reads the file text (`SimHost.DataDirectory`, relative to the Godot project) and passes it in, because the sim can't use Godot's `res://` paths.

- `weapons.json`: kind (bullet or shell), hit (direct or splash), damage, reload, range, shell speed, splash radius.
- `units.json`: members, speed, member health, weapon id, movement, and vehicle driving values (acceleration, braking, ease in and out, turn rates, reverse speed), whether it can capture, cost and build time.
- `buildings.json`: health, footprint size, the unit types it produces (in button order), queue limit, cost and build time, and what it becomes when finished (kind: building, post, or defense with its unit).
- A defense is a unit type with `static` movement: finished turrets are units, so they reuse targeting, turrets and firing. The sim drops every order to them but attack.
- The sim gets building types by id (`Simulation.BuildingTypes`, set by the host), because build commands carry type ids.
- Loading rejects bad data: an unknown weapon, unit or building type, a shell without speed, splash without a radius, a negative cost, a defense without a unit. A test loads the repo's real files.
- Why not TOML: game data nests (units with weapon lists, factions with unit lists), which TOML handles awkwardly. JSON needs no dependency, and this loader allows comments. TOML stays an option for flat settings.

## Rendering

- **Settings:**
  - [built] Forward+, AgX tonemapping, SSAO, one directional light with shadows, SMAA.
  - [decided] Add MSAA 2x only if silhouettes crawl while panning. Tune shadows to the camera range. Grade color through the Environment's correction texture. Keep post-processing light.
  - Rejected: TAA (it smears), real-time GI, lightmaps (the world is destructible), ray tracing, SSR.
  - Later: FSR 2 for weak hardware. CMAA2 is a deferred side project: render into a `SubViewport`, run compute passes through `RenderingDevice`, show the result via `Texture2DRD`.
- **The boundary is the cost:** a call from C# into the engine costs far more than one within C#. Batch per frame: `InstanceBatch` pushes one transform buffer per MultiMesh, for packages, pickups, shells and flashes. Unit views share materials per player, and views keep what they last drew in C# instead of reading engine properties back.
- **Scale plan:** one scene per unit is fine for the MVP. Numerous kinds move to MultiMesh, and very large counts to direct `RenderingServer` instances, behind the same view interface.
- **Measured at MVP scale** (stress scene: 200 units, 400 segments, 800 packages, a battle; dev machine, vsync off):
  - 330-560 fps; render at most ~1.1 ms CPU and ~0.9 ms GPU.
  - 485 draw calls idle, ~1,600 in battle.
  - Sim in Release: 0.1 ms/tick idle, 0.15 ms in battle (0.9 ms worst, likely return fire alerting a whole block of allies at once, each checking for targets). The editor runs C# in Debug, about 15× slower, so don't judge sim cost from it.
  - Nothing here blocks the MVP.
- **Rendering debt, in order:**
  1. **Belt segments:** a mesh and a material per segment, which is 400 of the 485 idle draw calls. Fix: one mesh per line with a state texture read by a shader. Do it in the visual pass, since the scrolling belt needs that shader anyway.
  2. **Health bars:** two quads and a material each, so most of the battle's extra calls. Fix: a MultiMesh with per-instance fill and color.
  3. **Placeholder mesh detail:** Godot's default capsules and cylinders are finely divided, about 1.6M triangles idle and 4.4M in battle. Real models fix it.
  4. **Squad members:** a node per member. Move them to a MultiMesh first when counts grow.
  5. **Per-frame rebuilds:** order paths and tracers rebuild an ImmediateMesh each frame. The HUD builds strings with LINQ each frame. The minimap redraws with canvas calls each frame (one per belt segment, building and post; packages and units batched per color) and allocates its point arrays. All fine through the MVP.

## Planned systems

- **Navigation** (in `Sim`, not Godot's navigation):
  - Cells: a grid (see the doctrine's open decisions) with per-cell cost (terrain, road), a blocked flag and a clearance value, so big vehicles avoid gaps meant for infantry.
  - Paths: A* with 8 directions plus smoothing, or Theta*. Lazy re-pathing when cells change. Flow fields for group moves. Local avoidance by separation first, RVO2-CS if crowds jam. Air ignores the grid.
  - Why no navmesh: runtime changes, several unit sizes, per-cell road costs, and the AI's threat and influence maps, flow fields and building placement all want a grid. A grid is also easier to keep deterministic and debug.
  - Candidate libraries: Roy-T.AStar, RVO2-CS.
- **AI** (in `Sim`, issuing the same commands as the player):
  - Commander: utility scoring over threat and influence maps, for economy, attacks and belt defense.
  - Squads and units: small state machines.
  - LimboAI is optional, for campaign scripting only.
- **Sim performance habits:**
  - Struct arrays, `Span<T>`, `ArrayPool` for temporary buffers, SIMD where it helps.
  - `Parallel.For` for independent work (pathfinding, visibility).
  - No ECS unless structure or speed starts hurting (then Arch or Friflo).
- **Tooling still to build:** a debug overlay for flow per segment and the grid and clearance view, alongside the current HUD counters. A baseline test machine.

## Testing

- `src/Sim.Tests` (xUnit, headless): sim rules, one behavior per test. Helpers are in `TestHelpers.cs`. Ad-hoc units made with `dps`/`range` get a bullet weapon that fires every tick, so damage timings stay exact. The win/lose edge-case table will live here too.
- `tools/input_smoke_test.gd`: real input events through `Input.parse_input_event` against the test map, `prototype.tscn`. It needs a window, because headless Godot drops input. It checks selection, formation, switch capture and flips, the hotseat camera, double-click, cursors, attack-move, order paths, camera controls, production (HQ selection, hotkeys, rally, cancel), construction (builder card, ghost, placing a foundation) and the minimap. It exits with the failure count.
  - The window is 1920×1080 over a 1152×648 design size (`canvas_items` stretch). Positions from `unproject_position` are in the design space, while injected events are window pixels, so its mouse helpers scale them up.
- `res://scenes/prototype.tscn -- --demo`: a scripted two-player match at 3x on the test map, with both HQs producing on repeat and Blue's selected, for unattended checks and movie-maker frames.
- `stress.tscn` with `-- --perf-log`: the performance numbers above.

## References

- OpenRA: an open-source C# C&C, grid-based; the primary code reference.
- The original C&C and Generals source code (GPL, EA, 2025).
- Game AI Pro (free): flow fields (Supreme Commander 2), commander AI and utility scoring.
- "1500 Archers on a 28.8" (Age of Empires lockstep).
- Game design books: *Designing Games*, *The Art of Game Design*, *Game Feel*, *Level Up!*. Also *Game Programming Patterns* and *Blood, Sweat, and Pixels*.
