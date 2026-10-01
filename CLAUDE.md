# Triple Point (working title)

C&C Generals-style RTS built around a shared, physical conveyor-belt economy. Godot 4.7.2 (.NET), C# on .NET 10.

## Where the truth lives

- `docs/doctrine.md`: design rules, each tagged [built], [decided], [proposed] or [idea]; MVP scope; open decisions; a **Rejected** list. Read it before design work. Don't re-propose anything on the rejected list without a new reason.
- `docs/architecture.md`: how the code is built, where things are, measured performance and rendering debt, planned systems (navigation, AI). Read it before structural work.
- **Numbers live in code and data, not in docs:** `data/*.json` and the constants at the top of `src/Sim/Simulation.cs`. Docs state rules.
- **Keep them true.** When a decision changes in conversation, update the doctrine in the same session. When you build something, flip its tag to [built]. When code and docs disagree, say so and fix whichever is wrong.

## Working rules

- **Never write story, lore, dialogue, mission narrative, character or faction names, or any other narrative content.** The story is written by the author alone. When story material is shared, critique it at the stage named (premise, outline, or written missions). Critique only, no rewrites.
- MVP first: the one question is whether fighting over the belt is fun. If something is outside MVP scope (doctrine), say so before building it. Core mechanics before visuals; don't sink time into details early.
- Every AI-generated or placeholder asset goes in [godot/assets/PLACEHOLDERS.md](godot/assets/PLACEHOLDERS.md).
- Verify end to end before calling something done: sim tests, the input smoke test, and a look at demo frames for anything visual.

## Architecture invariants

- `src/Sim` has **zero Godot references**. Never add one.
- A tick is `(state, commands) -> events` at a fixed 20 Hz. Player input and AI issue the same command objects, each carrying its issuing player.
- Godot views only read sim state and never change it. No game logic in per-node `_Process`. Selection, colors, names and the local player are Godot-side UI state.
- `System.Numerics` inside `Sim`; convert to Godot types only in `SimConvert`. One seeded RNG (`SimRandom`) owned by the sim; the AI has its own.
- **Deterministic for lockstep:** in `Sim`, `SimMath` for Sin, Cos, SinCos, Atan2 and Lerp, never MathF's or System.Numerics' Lerp/Reflect (they differ between CPUs); no clocks, threads, `System.Random` or `GetHashCode`. A new state field goes into `Simulation.Hash()`, a new command into `CommandCodec` (both tested), and a command field that comes over the network gets a check in `Simulation.Sane` or where it applies: a peer's game may be modified. Views' queries never change state, caches included (architecture: Determinism).
- No allocations or LINQ inside the tick. Batch calls across the C#/engine boundary; never read engine properties back just to compare them.
- Input goes through Input Map actions in `project.godot` (`select`, `select_add`, `act`, `queue_order`, `force_attack`, `speed_up`, `speed_down`, `attack_move`, `cancel`, `camera_left/right/forward/back`, `camera_zoom_in/out`, `camera_grab`, `debug_swap_player`, `slot_1`..`slot_5`, `stop`, `hold`, `auto_retreat`, `idle_builder`, `group_1`..`group_9`, `group_set_1`..`group_set_9`, `cancel_production`, `rotate_building`, `toggle_debug`, `pause_menu`), never literal keys or buttons in code.
- Player-facing text goes through `L.T("key", ...)` with the key in `godot/locale/en.po` (`LocaleTests` checks); never a literal string on screen. The sim holds no text.
- Game data is JSON in `/data` (`units.json`, `weapons.json`, `buildings.json`), parsed by `Sim` (`GameData`) with `System.Text.Json`, not Godot Resources. Godot reads the files (`SimHost.DataDirectory`) and passes the text in. Its schema is `docs/data.md`, checked by `DataSchemaTests`: change a record, its file and that page together.

## Current state

- **Built:** supply routes of neutral trucks on roads (in the code still belts: a route is a `BeltLine`, a truck a `Package`, a depot a `Gatherer`): roads of breakable pieces, trucks that queue at a break and stop for units in the way, trucks that can be shot (Ctrl+right-click) and spill their load, depots that unload a share of each passing truck that grows down the route (a sixth at home, half at the tail), routes that start full, finite sources that take back what reaches the end (a gauge per source), pickups (hops, shards, collection) and paid repair (builders and engineers, auto-repair nearby); depots built by players, spaced along the road, with free spots shown while placing; routes as plain lines, no junctions, coming from beyond the map through covered stretches; road types (dirt and paved: paved pieces 3× as tough and faster to move on; builders and engineers pave dirt, paid per piece; the main map paved from home through the crossing, dirt along the tail); road and truck visuals (asphalt with edge and center lines, dirt with ruts, verges, craters, crates on the bed, wrecks, hover hints); two players, each starting with an HQ, a builder and 50 packages; production at buildings (paid as they build, one queue with no real limit, Shift queues or cancels five, rally point); mending at home (infantry at a barracks, vehicles at a factory, paid; nothing heals on its own), repairing your buildings, depots and defenses (builders and engineers), an auto-retreat toggle; garrison houses (neutral until infantry go in by right-click; Exit for all, or a squad's tile for one; those inside fire out further, hidden, and the house takes the hits; capacity per type in data; two at the crossing on the main map); a construction prototype (builder, barracks, factory, post, turret after a barracks, heavy anti-tank turret after a factory, range rings; placement anywhere, grid-snapped, posts snapped beside belts; needs the whole cost to start, paid as it grows); win/lose (`CanStillRecover`: production buildings keep a side in, depots don't; a 60 s rebuild clock, game-over banner); a corner-panel interface of fixed sizes (a top strip with packages and alerts, a 4×3 positional command grid on physical keys, a selection panel with a tile per type, a minimap, an idle-builder button); stop, hold position and control groups; a one-line HUD with debug text under F3; a rifle squad, a rocket squad, an engineer, a scout car, a tank and artillery (stops to fire, minimum range, holds ground, range rings); data-driven weapons (bullets and shells, direct and splash, ballistic arcs with scatter; only splash breaks road; armor as a factor per target class, `against`: rifles beat rockets beat tanks beat rifles), a direct hit fells one squad member at most, schema in `docs/data.md`; eased vehicle driving with sim turrets (optionally arc-limited, the hull swings round); vehicle wrecks (view only); fire on the move; return fire (leashed, allies join); attack-move (goes for what it sees within range + 8 m); order paths for the selection, colored by order; readable looks (neutral bodies with side-colored helmets, turret tops and roofs; a silhouette per class; health bars when damaged or selected); cursors; F2 hotseat; lockstep multiplayer, 1v1 over direct IP (`Lockstep`, `NetMessage`, `NetSession`; a menu page to host or join, the handshake checks build and data, hashes compared every second, a desync stops both and writes a report naming the first difference; deterministic math in `SimMath`, a state hash and dump, replays); a main menu (skirmish setup: your side, Easy or Normal AI or hotseat; a controls page; fullscreen and interface size in `user://settings.cfg`), reached again from pause and game over; RTS camera; the stress scene; a scripted commander AI (`src/Sim/Ai`, Red on the main map, `--ai=`, levels with `--ai-level=easy|normal`; it holds the house on its half, guards a depot with a turret, and varies from seed to seed); match reports (`MatchStats`, `MatchReport`: a text file per match in `user://matches`, i.e. `%APPDATA%/Godot/app_userdata/Triple Point/matches`); navigation (1 m cells, A* round buildings, posts, turrets and rocks by unit radius, soft pushing; `Navigation.cs`, `NavGrid.cs`); fog of war (radius sight on 2 m cells, remembered enemy buildings and belt, a belt-cut alert, firing needs your side's sight, shooters revealed for 3 s; `Vision.cs`, `Sight`, `FogOverlay`; `--reveal` shows everything).
- **Next in the MVP:** the friends playtest, against each other over the network or against the AI (doctrine: MVP 7, 8). The AI becomes a behaviour tree after v1.

## Layout

```
Game.sln            root solution; Godot uses it (dotnet/project/solution_directory = res://..)
src/Sim/            simulation library, net10.0
src/Sim.Tests/      xUnit, headless
src/Sim.Runner/     headless AI match or replay check on the main map, printing state hashes (the cross-JIT test runs it)
godot/              Godot project: scenes/ (menu, main, prototype, stress), views/ (unit), scripts/, assets/PLACEHOLDERS.md
data/               units.json, weapons.json, buildings.json
docs/               doctrine.md, architecture.md, data.md (story documents are kept out of the repo)
tools/              input smoke test, main map generator, frame capture (snap.gd), texture atlas builder, playtest build (export_build.py)
```

## Maps

- `menu.tscn` is the project's main scene: the main menu, whose Skirmish starts `main.tscn` with the chosen side and opponent (`MatchSetup`). A map scene run directly uses its own exports.
- `main.tscn` is the playable map; its design rules are in the doctrine (Infrastructure and construction, Playable map). Its map nodes are generated: change the layout in `tools/make_main_map.py` and run `python tools/make_main_map.py`, which rewrites only the map nodes (hand edits to them get overwritten).
- `prototype.tscn` is the small test map that the input smoke test and `--demo` are written against. Don't redesign it without updating both. Each side starts with two squads, a tank and artillery; the artillery is parked 30 m back, out of the other's reach, because at 28 m range it would start the fight on its own.
- `stress.tscn` has no authored content; `StressMap` generates it.

Authoring, in any map scene:

- `Belts`: `Path3D` curves become supply routes (cut into 5 m breakable road pieces at load), each from its source (the curve's start) to its end, on the ground (height 0). With the `BeltPath` script, `CoveredStart` and `CoveredEnd` (m) cover its ends: unbreakable, no depots, trucks hidden; road up to `PavedTo` (m) is paved, the rest dirt. Start and end routes beyond the map edge, covered. SimHost's `BeltSpeed`, `SpawnInterval`, `PackageSpacing`, `TruckLoad`, `TruckHealth` and `StartFull` set the trucks.
- `Gatherers`: `OwnedMarker`s (with `Player`) beside a road become that player's depots.
- `Units`: `UnitSpawn`s (with `Player` and a `UnitType` from `data/units.json`) are the starting units; a marker's -Z is the unit's facing.
- `Buildings`: `BuildingSpawn`s (with `Player` and a `BuildingType` from `data/buildings.json`) are the starting buildings; a marker's -Z is the exit side. `Player = -1` makes a neutral one (a garrison house). `SimHost.StartingPackages` is each player's starting money.
- `Obstacles` (SimHost's `Obstacles`): `MapObstacle` nodes are rocks, boxes of `Size` turned with the node, solid for navigation and building.
- The camera's `Bounds` are the map: navigation covers them and nothing leaves them.
- The main map's generator checks its layout against the map rules (one safe depot slot per route, contested tails, neutral ends, symmetry) and refuses to write a map that fails; `--check` prints the report only.

## Commands

```bash
dotnet build Game.sln
```

```bash
dotnet test Game.sln
```

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path godot --build-solutions --quit
```

Input smoke test (needs a window; headless drops input). Exits with the failure count. The window opens off-screen, because a real cursor over it is input too and makes scripted clicks land wrong:

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --path godot --fixed-fps 60 --position -10000,-10000 -s ../tools/input_smoke_test.gd
```

Stress scene (generated belts, 200 units, a battle). The HUD's Perf line shows fps, sim ms/tick, render CPU/GPU and draw calls; `--perf-log` prints it once a second. The editor runs C# in Debug, about 15× slower in the sim than Release:

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --path godot res://scenes/stress.tscn --disable-vsync -- --perf-log
```

Unattended visual check: `res://scenes/prototype.tscn -- --demo` plays a scripted two-player match at 3x on the test map (a road raid, a truck shot at the break and its load collected, a repair, depots destroyed, a fight, both HQs producing, the ending), logging events. To look at frames, capture only the ones needed (seconds, not minutes; needs a window). `--fixed-fps` makes frame N the same moment every run; `--camera=x,z,distance` and `--set=Node.Property=value` apply before the scene starts:

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --path godot --fixed-fps 60 --disable-vsync -s ../tools/snap.gd -- --scene=res://scenes/prototype.tscn --frames=600,1040 --out=<dir> --demo
```

A playtest build: `python tools/export_build.py` builds the C#, exports the `Windows Desktop` preset to `build/windows` (the .NET runtime bundled, `data/` beside the executable), adds `HOW-TO-PLAY.txt` and zips it to `build/TriplePoint-windows-<commit>.zip`. Needs the 4.7.2 .NET export templates (Windows x86_64 is installed). A release build takes no scene on the command line: `-- --start=red,normal` (blue or red; easy, normal or hotseat) starts a match from the menu, and `--ai=` still applies:

```bash
build/windows/TriplePoint.exe --position -10000,-10000 --fixed-fps 60 --quit-after 4500 -- --start=blue,normal --ai=0,1
```

Two game processes in a networked match over localhost, the AI playing each side, comparing hashes (`--fault-at=<tick>` tests the desync report instead; `--exe=build/windows/TriplePoint.exe` uses the export):

```bash
python tools/net_test.py --minutes=3
```

Headless determinism check: an AI match on the main map, printing state hashes (`--sections` for each section every line, `--record=<file>` saves a replay, `--replay=<file>` checks one):

```bash
dotnet run -c Release --project src/Sim.Runner -- --seed=1 --minutes=10 --every=1200
```

Check the build's output, never discard it: when the C# build fails, Godot quietly runs the last good build.
