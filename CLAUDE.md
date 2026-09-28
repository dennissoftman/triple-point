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
- `System.Numerics` inside `Sim`; convert to Godot types only in `SimConvert`. One seeded RNG (`SimRandom`) owned by the sim.
- No allocations or LINQ inside the tick. Batch calls across the C#/engine boundary; never read engine properties back just to compare them.
- Input goes through Input Map actions in `project.godot` (`select`, `select_add`, `act`, `queue_order`, `force_attack`, `speed_up`, `speed_down`, `attack_move`, `cancel`, `camera_left/right/forward/back`, `camera_zoom_in/out`, `camera_grab`, `debug_swap_player`, `produce_1/2/3`, `produce_repeat`, `cancel_production`), never literal keys or buttons in code.
- Game data is JSON in `/data` (`units.json`, `weapons.json`, `buildings.json`), parsed by `Sim` (`GameData`) with `System.Text.Json`, not Godot Resources. Godot reads the files (`SimHost.DataDirectory`) and passes the text in.

## Current state

- **Built:** belts with breakable segments, spill and repair; gatherer posts; merges and switches (split while neutral, captured by squads, turned to the captor's side, flipped by the owner); two players, each with an HQ that produces units (paid as they build, one queue, repeat, rally point); a rifle squad, a scout car and a tank; data-driven weapons (bullets and shells, direct and splash); eased vehicle driving with sim turrets; fire on the move; return fire (leashed, allies join); attack-move; order paths for the selection, colored by order; cursors; F2 hotseat; RTS camera; the stress scene.
- **Next in the MVP:** win/lose (`CanStillRecover`), then the commander AI.

## Layout

```
Game.sln            root solution; Godot uses it (dotnet/project/solution_directory = res://..)
src/Sim/            simulation library, net10.0
src/Sim.Tests/      xUnit, headless
godot/              Godot project: scenes/ (main, stress), views/ (unit), scripts/, assets/PLACEHOLDERS.md
data/               units.json, weapons.json, buildings.json
docs/               doctrine.md, architecture.md (story documents are kept out of the repo)
tools/              input smoke test
```

## Map authoring (godot/scenes/main.tscn)

- `Belts`: `Path3D` curves become belt lines (cut into 5 m breakable segments at load).
- `Junctions`: markers; belt ends within 1 m attach (a line's end is an input, its start an output).
- `Gatherers`: `OwnedMarker`s (with `Player`) beside a belt become that player's posts.
- `Units`: `UnitSpawn`s (with `Player` and a `UnitType` from `data/units.json`) are the starting units; a marker's -Z is the unit's facing.
- `Buildings`: `BuildingSpawn`s (with `Player` and a `BuildingType` from `data/buildings.json`) are the starting buildings; a marker's -Z is the exit side. `SimHost.StartingResources` is each player's starting money.
- Keep posts and spawns out of range of capture points: at least the longest weapon range plus the capture radius.
- `stress.tscn` has no authored content; `StressMap` generates it.

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

Input smoke test (needs a window; headless drops input). Exits with the failure count:

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --path godot --fixed-fps 60 -s ../tools/input_smoke_test.gd
```

Stress scene (generated belts, 200 units, a battle). The HUD's Perf line shows fps, sim ms/tick, render CPU/GPU and draw calls; `--perf-log` prints it once a second. The editor runs C# in Debug, about 15× slower in the sim than Release:

```bash
"C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" --path godot res://scenes/stress.tscn --disable-vsync -- --perf-log
```

Unattended visual check: `-- --demo` plays a scripted two-player match at 3x (switch captures, a post destroyed, a fight, both HQs producing), logging events. Add `--write-movie <dir>/f.png --fixed-fps 10 --quit-after 340` to capture frames; use `--fixed-fps 60` to see sub-second effects such as shells.
