# Architecture

How the code is built, and the technical plans that aren't code yet. The invariants every change must keep are in `CLAUDE.md`; this document explains them and maps the code. Update it when a structure changes. Design rules are in `docs/doctrine.md`.

## Stack

- Godot 4.7.2 .NET, C# on .NET 10. Every project targets `net10.0`. Desktop C# runs on CoreCLR with a JIT (tiered compilation, dynamic PGO). There is no web export.
- `Game.sln` at the root is the solution Godot uses (`dotnet/project/solution_directory = res://..`). `godot/Game.csproj` references `src/Sim/Sim.csproj`.
- Code names stay neutral (`Game`, `Sim`) so a rename of the game touches nothing.

## Sim and view

**Names:** the economy is supply trucks on roads (doctrine: Supply routes), but the code still says belt: a route is a `BeltLine` in `State.Belts`, a road piece a `BeltSegment`, a truck a `Package` in `line.Packages`, a depot a `Gatherer`. They get renamed in one mechanical commit once the trucks prove themselves; player-facing text already says route, road, truck and depot.

`src/Sim` is a plain class library with no Godot reference. It owns all game state and rules. The Godot project is a host and a set of views over it.

- **One tick:** `Simulation.Tick(commands) -> events` at a fixed 20 Hz, advancing `SimState` in place. The returned event list is reused and valid until the next call.
- **Commands** (`Commands.cs`) are records carrying their issuing player: move, attack, attack-move, attack segment, repair segment, break segment (scripted), build and resume building (builders), repair a building, depot or defense, mend at a building, stop, hold, set auto-retreat, and for buildings produce (a count at once), cancel production, set rally. Player input and the AI issue the same commands. The sim drops commands to units and buildings the issuer doesn't own.
- **Events** (`SimEvent`) say what happened, for logs and effects: unit died, arrived or produced, package lost or gathered (by truck), truck destroyed, gatherer or building destroyed, building placed or completed, build blocked, segment broken or repaired, shell hit, and the win/lose events (grace started or ended, player lost, game over).
- **Tick order:** apply commands, re-mark the navigation grid if anything solid changed, then update units (orders, movement along paths, turrets, shots), land the tick's bullets, push units apart, move shells, remove the dead (a destroyed truck spills here), move the trucks, depots unload them, construction, production, end conditions, send trucks from the sources, pickups, and last each side's sight. Builders only mark the foundation they work on during the unit update; construction grows it later in the tick, so a finished defense can add a unit without changing the unit list under the unit loop.
- **Fairness within a tick:** units update in list order, forward one tick and backward the next, and bullets land after every unit has fired, so two units that shoot each other on the same tick both get their shot off. A shooter is revealed to its victim's side from the next tick. Together with ties never broken by map direction, this keeps mirror matches even (`AiTests` checks that both sides win some).
- **State** (`SimState.cs`): `Unit` and `Gatherer` are structs in lists, updated through spans. Belt lines (routes), segments and buildings are classes. Trucks (`Package`) are structs ordered front to back per line. Pickups and projectiles are unordered, removed by swapping with the last. Ids come from one counter per sim.
- **Randomness:** one seeded `SimRandom` (xorshift). Math goes through `System.Numerics`, and conversion to Godot types happens only in `SimConvert`. Determinism isn't required yet, but nothing rules it out: fixed-point math could replace floats later for lockstep.
- **Performance:** no allocations or LINQ inside the tick. Target search is all-pairs for now; pushing uses 4 m buckets, and target search can move onto them when it shows up in a profile.

## Where things are

| Concern | Sim (`src/Sim`) | View and host (`godot/scripts`) |
|---|---|---|
| Units, orders, movement, easing, turrets, weapons, armor, shells, splash, return fire | `Simulation.cs` (units section; `InFiringRange` with the approach margin, `AimAt` with the turret arc, `Hit`, `Splash` and `Armor` with a weapon's `Against`, `SplashBelts`), `SimState.cs` (`Against`) | `UnitsView` (instances, order paths of the selection, tracers, shells with arcs, smoke and shadows, flashes, range rings, wrecks), `UnitView` (one unit's look, barrel elevation, suspension lean, wreck), `UnitMaterials`, `HealthBar` |
| Repair and auto-repair | `Simulation.cs` (`PayForRepair`, `FindDamagedSegment`), `SimState.cs` (`BeltSegment.RepairCredit`, `Player.Spent`) | `PlayerInput` (repair intent only with a repairer selected) |
| Supply routes: trucks, finite sources, depots, depot spacing, breaks, shot trucks and their spill, pickups, covered stretches | `Simulation.cs` (`AddTruck` with `StartFull`, `SpawnPackage` and `MovePackages` with the reserve, `InTheWay` (units holding trucks up, idle ones stepping aside), `UpdateGatherers` (a depot's share, `DepotShare`, growing down the route; `ShareAt` for placing), `SpillCargo`, `FindTruck`, `UpdatePickups`, `TooCloseToPost`, `PostSpots`, `SnapPost`; `FindSegment(openOnly)`), `SimState.cs` (`BeltConfig`, `Package`, `BeltSegment.Covered`), `BezierSegment.cs` | `BeltView` (roads and housings, craters, health bars, hover, depot spots; source gauges; trucks and their crates, truck bars and wrecks, spill hops, shards and collection +1s, depots), `BeltPath` (a map's route, with its covered ends), `PlayerInput` (Ctrl+right-click a truck; its hint), `InstanceBatch` |
| Mending, repairing structures, auto-retreat | `Simulation.cs` (`Mend`, `RepairStructure`, `Reach`, `PayFor`, `NearestMender`; the auto-retreat check at the top of the unit update), `SimState.cs` (`BuildingType.Mends`, `Unit.AutoRetreat`) | `PlayerInput` (right-click your barracks or factory, or a damaged building with a repairer; `ToggleRetreat`), `CommandCard` (the X cell) |
| Buildings, production queue, rally points | `Simulation.cs` (`UpdateProduction`), `SimState.cs` (`Building`) | `BuildingsView` (blocks, bars, rally flag), `CommandCard` |
| Construction: builders, foundations, placement, grid snap, depots snapped to roads | `Simulation.cs` (`Construct`, `UpdateConstruction`, `CanPlace`, `CanAfford`, `SnapToGrid`, `SnapToBelt`) | `PlayerInput` (placement), `BuildingsView` (foundations, ghost), `CommandCard` |
| Navigation: ground cells, clearance, A* with straightened paths, pushing apart, obstacles | `NavGrid.cs` (cells, clearance, `FindPath`, `LineClear`), `Navigation.cs` (the Simulation's part: `EnableNavigation`, `AddObstacle`, `Move` along paths with a per-player search budget, `Separate`), `SimState.cs` (`Obstacle`, `Unit.Radius`) | `SimHost` (the camera's bounds are the map; `MapObstacle` nodes under `Obstacles`), `MapObstacle` (a rock's look), `UnitsView` (order paths along the path), `Minimap` (rocks) |
| Fog of war: sight, memory of enemy buildings and roads, firing on sight, reveal | `Vision.cs` (the Simulation's part: `UpdateVision` stamps each side's sight discs on 2 m cells after the tick; `PlayerVision` keeps last-seen ticks, `Ghosts` and per-segment memory; `Sees`, `SeesUnit`, `Knows`, `GiveAway`, `TellCutOff`, `CopyVisibility`), `SimState.cs` (`Unit.Sight`, `RevealUntil`) | `Sight` (what the local player may see, for every view; `All` under `--reveal`, the demo or fog off), `FogOverlay` (a full-screen pass that shades unseen ground from the depth buffer; its texture also shades the minimap), `SimHost` (`FogOfWar`, the belt-cut alert), and each view hiding what isn't seen |
| Garrison buildings | `Simulation.cs` (`Enter`, `Leave` (one squad or all: `ExitCommand.UnitId`), `GarrisonRange`, `EjectedHealth`; units inside skipped by targeting, splash, hits, pushing and trucks; empty ones skipped as targets), `SimState.cs` (`BuildingKind.Garrison`, `BuildingType.Garrison`, `Building.Occupants`, `Unit.Inside`), `Vision.cs` (those inside unseen) | `PlayerInput` (right-click a house with infantry: `GarrisonAt`, `KnownOwner`; `ExitGarrison`), `CommandCard` (Exit), `SelectionPanel` (a tile per squad inside), `BuildingsView` (house, holder's color, pips), `UnitsView` (hidden inside, tracers from the walls) |
| Commander AI | `Ai/Commander.cs` (survey, build, produce, fight, mend, ambush, occupy, collect, scout; `AiLevel`, `AiSettings`), `Ai/AiView.cs` (what it may know: its side's sight and memory) | `SimHost` (`AiPlayers`, `--ai=`, `AiLevel`, `--ai-level=`; adds its commands to each tick) |
| Match reports and the event log | `MatchStats.cs` (fed after each tick, outside it: totals, samples every 30 s, firsts; who owned what by id), `SimState.cs` (`BeltSegment.BrokenBy`) | `MatchReport` (formats and writes `user://matches/match-<time>.txt`), `SimHost` (`MatchReports`; writes at game over or on leaving; `Log` names owner and type) |
| Win and lose: rebuild clock, losing, game over | `Simulation.cs` (`UpdateEndConditions`, `CanStillRecover`, `Lose`; `EndConditions` switches it on), `SimState.cs` (`Player.GraceTicksLeft`, `Lost`; `GameOver`, `Winner`) | `GameOverOverlay` (banner, Restart, Quit), the HUD line (rebuild clocks) |
| Minimap | | `Minimap` (canvas drawing; click, drag, right-click) |
| Pause menu | | `PauseMenu` (sets `SimHost.Paused`; `PlayerInput` opens it on `cancel` with nothing to cancel, and takes no input while it's open) |
| Local player's packages and income | | `PackagePanel` (top-center; income from a 30 s ring buffer of what was gathered and collected) |
| Interface layout: top strip, selection panel, command grid, idle builders, fixed sizes | `Commands.cs` (`StopCommand`, `HoldCommand`), `Simulation.cs` (`Halt`), `SimState.cs` (`Unit.Holding`) | `SimHost.BuildInterface` (creates them), `TopBar` (strip and alerts), `SelectionPanel` (one unit, a building, or a tile per type), `CommandCard` (the 4×3 grid: cells by physical key, queue badge, progress bar), `IdleBuilderButton`, `UiSizes` (reserves the width of the widest text a label will show), `PlayerInput` (control groups, stop, hold, idle builder). Every widget caches what it shows in C# and pushes only changes to the engine |
| Game data | `GameData.cs` (parses `data/units.json`, `data/weapons.json`, `data/buildings.json`); the schema is `docs/data.md` | `SimHost.LoadData` reads the files |
| Host: ticks, game speed, map building, HUD (one line, debug text under `toggle_debug`), perf readout, demo script | `DestroyCommand` (a host-only tool: the demo's scripted kills) | `SimHost` |
| Input, selection (units, or one building), placement, cursor, hotseat | | `PlayerInput`, `Cursors` |
| Camera | | `RtsCamera` (`CameraView`, `FlyTo`) |
| Map markers | | `OwnedMarker`, `UnitSpawn`, `BuildingSpawn`; `StressMap` generates the stress scene |

- **The host:** `SimHost._Process` adds real time multiplied by the game speed to an accumulator, runs the ticks that are due, logs events, and hands them to `UnitsView.OnEvent`. Then it syncs the views with `alpha`, the fraction of the way to the next tick, so movement interpolates smoothly.
- **Views read, never write:** views read `SimState` and never change it, and no game logic runs in per-node `_Process`. Units carry `Prev*` fields (position, heading, turret) for interpolation.
- **UI state stays on the Godot side:** selection, colors, names and the local player live in the views and input code; the sim only sees commands.
- **Input:** Input Map actions only, never literal keys. What a click would do is one `Intent`, computed once and used for both the cursor and the command, so the two can't disagree. `PlayerInput` tracks the mouse from its own events (the OS cursor position is wrong for injected events).
- **Godot C# conventions:** script classes are `partial` and named after their file. Prefer `[Export]` fields over `GetNode("path")`. Commit the `.uid` files.

## Text and languages

- Player-facing text goes through `L` (`godot/scripts/L.cs`): `L.T("card.cost", ...)` looks a symbolic key up in `godot/locale/<lang>.po` and formats it with .NET placeholders; unit and building names are `unit.<id>` and `building.<id>`, so `/data` holds no text. English (`en.po`) is also the fallback for any key a language lacks.
- The sim holds no text at all: it returns codes (why placement failed, which event), and views word them.
- `LocaleBoot` (an autoload) sets the language before any scene builds its UI: `--lang=xx` (saved to `user://settings.cfg`), else the saved choice, else the OS's.
- `LocaleTests` keeps the code and `en.po` in step both ways (a quoted string starting with a key family, such as `"card.`, is a key), checks every unit and building has a name, and that no language has keys English lacks. The F3 debug text stays English.
- A new language: copy `en.po`, translate, add it to `internationalization/locale/translations`. Fonts: the default covers Latin and Cyrillic; CJK needs a fallback font.

## Game data

JSON in `/data`, parsed by `Sim` with `System.Text.Json`: comments, trailing commas, case-insensitive names, enums as strings. Godot reads the file text (`SimHost.DataDirectory`, relative to the Godot project) and passes it in, because the sim can't use Godot's `res://` paths.

- **The schema is [data.md](data.md):** every field of the three files, its unit, default and meaning. Parsing is strict (an unknown field fails the load), and `DataSchemaTests` checks the tables against the records' constructor parameters both ways, so the page can't drift from the code. Change the record, the file and the page together.
- A defense is a unit type with `static` movement: finished turrets are units, so they reuse targeting, turrets and firing. The sim drops every order to them but attack, and ends an attack once the target is out of reach. Building prerequisites are `BuildingType.Requires`, checked by `Simulation.HasRequired` when the foundation is laid (`BlockedByRequirement`) and by `PlayerInput` and `CommandCard` up front.
- The sim gets building types by id (`Simulation.BuildingTypes`, set by the host), because build commands carry type ids.
- Loading rejects bad data: an unknown field, an unknown weapon, unit or building type, a shell without speed, splash without a radius, a minimum range not below the range, ballistic or scatter on a bullet, a negative armor factor, a negative cost, a defense without a unit. A test loads the repo's real files.
- Why not TOML: game data nests (units with weapon lists, factions with unit lists), which TOML handles awkwardly. JSON needs no dependency, and this loader allows comments. TOML stays an option for flat settings.

## Rendering

- **Settings:**
  - [built] Forward+, AgX tonemapping, SSAO, one directional light with shadows, SMAA.
  - [decided] Add MSAA 2x only if silhouettes crawl while panning. Tune shadows to the camera range. Grade color through the Environment's correction texture. Keep post-processing light.
  - Rejected: TAA (it smears), real-time GI, lightmaps (the world is destructible), ray tracing, SSR.
  - Later: FSR 2 for weak hardware. CMAA2 is a deferred side project: render into a `SubViewport`, run compute passes through `RenderingDevice`, show the result via `Texture2DRD`.
- **Textures** (placeholders): `tools/make_textures.py` builds `godot/assets/textures/` from the source sheets in `godot/assets` (the belt atlas: horizontal bands, each one tile wide; the crate atlas: 4 x 3 faces). Both import as VRAM-compressed (DXT1 on desktop) with mipmaps. Without the files the views fall back to flat colors.
  - Roads lie on the ground (maps author their curves at height 0); the sim measures distances to them on the ground, and `PlayerInput` picks pieces on the plane at their height (`RtsCamera.PointAt`).
  - Road pieces use one small shader (`BeltView.SegmentShader`): vertex colors (times the atlas, for housings only), a damage tint and a hover glow as uniforms. Its scrolling surface band, on a global `belt_time`, was the belt's and stands still for roads. One material per piece; draw calls are unchanged.
  - Trucks are one MultiMesh of a single vertex-colored body mesh (`BeltView.BuildTruck`). Their crates, pickups and shards share the crate shader: each instance's crate comes from MultiMesh custom data (`InstanceBatch(customData: true)`), chosen from an id.
- **The boundary is the cost:** a call from C# into the engine costs far more than one within C#. Batch per frame: `InstanceBatch` pushes one transform buffer per MultiMesh, for trucks, their crates, pickups, shells and flashes. Unit views share materials per player, and views keep what they last drew in C# instead of reading engine properties back.
- **Scale plan:** one scene per unit is fine for the MVP. Numerous kinds move to MultiMesh, and very large counts to direct `RenderingServer` instances, behind the same view interface.
- **Measured at MVP scale** (stress scene: 200 units, 400 segments, 800 packages on belts then, about 150 trucks now; a battle; dev machine, vsync off):
  - At 1920×1080: about 300-350 fps; render at most ~1 ms CPU and ~1 ms GPU. (At 1152×648 it was 330-560 fps.)
  - 485 draw calls idle, ~1,600 in battle.
  - Sim in Release: 0.1 ms/tick idle, 0.15 ms in battle (0.9 ms worst, likely return fire alerting a whole block of allies at once, each checking for targets). The editor runs C# in Debug, about 15× slower, so don't judge sim cost from it.
  - Nothing here blocks the MVP.
  - With navigation and fog, the Debug sim runs about 2.4 ms/tick in the stress battle (1.8 ms before fog); not yet measured in Release.
- **Rendering debt, in order:**
  1. **Road pieces:** a mesh and a material per piece, which is 400 of the ~415 idle draw calls in the stress scene (a broken one swaps to two halves, a crater and a few debris boxes). Fix: one mesh per route with a state texture read by a shader, in the visual pass.
  2. **Health bars:** two quads and a material each, so most of the battle's extra calls. Fix: a MultiMesh with per-instance fill and color.
  3. **Placeholder mesh detail:** Godot's default capsules and cylinders are finely divided, about 1.6M triangles idle and 4.4M in battle. Real models fix it.
  4. **Squad members:** a node per member. Move them to a MultiMesh first when counts grow.
  5. **Per-frame rebuilds:** order paths and tracers rebuild an ImmediateMesh each frame. The HUD builds strings with LINQ each frame. `FogOverlay` blurs and re-uploads its texture each frame (one byte per 2 m cell). The minimap redraws with canvas calls each frame (belts batched into two line lists rebuilt only when a segment breaks or is repaired; one call per building and post; packages and units batched per color) and allocates its point arrays. All fine through the MVP.

## Planned systems

- **Navigation** (in `Sim`, not Godot's navigation):
  - Built: 1 m cells over the map (the camera's bounds), solid under buildings, foundations, depots, defenses and obstacles. Each cell stores its clearance (a two-pass chamfer distance transform, negative inside solid ground), so one grid serves every unit radius and the push-out follows its gradient. The grid is re-marked from scratch whenever something solid appears or goes (a fraction of a millisecond), and paths planned on an older grid check their remaining legs before re-planning.
  - Paths: A* over cells the unit fits in (8 directions, no corner cutting, octile heuristic with a tiny off-the-line term so ties don't go by map direction), then pulled straight by line of sight; a clear straight line skips the search. Each player gets 12 searches a tick; the rest wait a tick or two. Pushing is soft separation in 4 m buckets.
  - Next: flow fields for group moves; RVO2-CS only if crowds jam; per-cell costs with roads. Air ignores the grid.
  - Why no navmesh: runtime changes, several unit sizes, per-cell road costs, and the AI's threat and influence maps, flow fields and building placement all want a grid. A grid is also easier to keep deterministic and debug.
- **AI** (in `Sim`, issuing the same commands as the player):
  - Built (v1): `Sim.Ai.Commander`, scripted (see the doctrine). It thinks every 20 ticks at normal level (40 easy), the same ticks for every player at a level (a tick apart handed one side a steady edge in mirror matches), and returns commands the host adds to the tick's list, like input. It reads the world only through `AiView`: its side's sight and memory under fog of war, the same as a player's. It's deterministic and doesn't allocate once warm. `SimHost.AiPlayers` (main map: Red) or `--ai=1`, `--ai=0,1`, `--ai=none` picks who it plays; F2 onto an AI player gives you both hands. `AiTests` plays it on the real main map (`MainMap.Load` reads `main.tscn`), mirror matches and each level against the one below.
  - Next: a behaviour tree (doctrine), with `AiView` as the blackboard; utility scoring over threat and influence maps (on the navigation grid) chooses between branches.
  - Squads and units: small state machines.
  - LimboAI is optional, for campaign scripting only.
- **Sim performance habits:**
  - Struct arrays, `Span<T>`, `ArrayPool` for temporary buffers, SIMD where it helps.
  - `Parallel.For` for independent work (pathfinding, visibility).
  - No ECS unless structure or speed starts hurting (then Arch or Friflo).
- **Tooling still to build:** a debug overlay for flow per segment and the grid and clearance view, alongside the current HUD counters. A baseline test machine.

## Testing

- `src/Sim.Tests` (xUnit, headless): sim rules, one behavior per test. Helpers are in `TestHelpers.cs`. Ad-hoc units made with `dps`/`range` get a bullet weapon that fires every tick, so damage timings stay exact. The win/lose edge-case table is `EndConditionsTests`.
- `tools/input_smoke_test.gd`: real input events through `Input.parse_input_event` against the test map, `prototype.tscn`. It needs a window, because headless Godot drops input. It checks selection, formation, breaking an enemy segment and the belt cursors, the hotseat camera, double-click, cursors, attack-move, order paths, camera controls, production (HQ selection, hotkeys, rally, cancel), construction (builder card, ghost, placing a foundation) and the minimap. It exits with the failure count.
  - The window is 1920×1080 over a 1152×648 design size (`canvas_items` stretch). Positions from `unproject_position` are in the design space, while injected events are window pixels, so its mouse helpers scale them up.
- `res://scenes/prototype.tscn -- --demo`: a scripted two-player match at 3x on the test map, with both HQs producing and Blue's selected, for unattended checks and movie-maker frames.
- `stress.tscn` with `-- --perf-log`: the performance numbers above.

## References

- OpenRA: an open-source C# C&C, grid-based; the primary code reference.
- The original C&C and Generals source code (GPL, EA, 2025).
- Game AI Pro (free): flow fields (Supreme Commander 2), commander AI and utility scoring.
- "1500 Archers on a 28.8" (Age of Empires lockstep).
- Game design books: *Designing Games*, *The Art of Game Design*, *Game Feel*, *Level Up!*. Also *Game Programming Patterns* and *Blood, Sweat, and Pixels*.
