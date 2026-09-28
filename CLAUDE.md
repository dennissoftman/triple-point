# Triple Point (working title)

C&C Generals-style RTS built around a shared, physical conveyor-belt economy. Godot 4.7.2 (.NET), C# on .NET 10.

The full design, decisions and MVP scope live in [docs/rts-handoff.md](docs/rts-handoff.md). Read it before design or architecture work, and flag any drift between it and the code.

## Working rules

- **Never write story, lore, dialogue, mission narrative, character or faction names, or any other narrative content.** The story is written by the author alone. When story material is shared, critique it at the stage named (premise, outline, or written missions). Critique only, no rewrites.
- MVP first (handoff section 8). If something is outside MVP scope, say so before building it.
- Every AI-generated or placeholder asset goes in [godot/assets/PLACEHOLDERS.md](godot/assets/PLACEHOLDERS.md).

## Architecture rules

- `src/Sim` has **zero Godot references**. Never add one. It's a plain class library: units, squads, grid, belt, economy, orders, AI, rules.
- A tick is `(state, commands) -> (newState, events)`, at a fixed 20 Hz. Player input and AI issue the same command objects.
- Godot views only read sim state and never change it. No game logic in per-node `_Process`.
- Use `System.Numerics` (or our own types) inside `Sim`. Convert to Godot types in one place, at the boundary.
- One seeded RNG owned by the sim, and all math routed through one place, so determinism stays possible later.
- No allocations or LINQ inside the tick.
- Batch calls across the C#/engine boundary. They cost far more than calls within C#.
- Input goes through Input Map actions defined in `project.godot` (`select`, `select_add`, `act`, `queue_order`, `force_attack`, `speed_up`, `speed_down`), never literal keys or buttons in code.
- Game data is JSON in `/data`, loaded by `Sim` with `System.Text.Json`, not Godot Resources. The Godot side resolves `res://` to a real path and passes it in.

## Layout

```
Game.sln            root solution; Godot uses it (dotnet/project/solution_directory = res://..)
src/Sim/            simulation library, net10.0
src/Sim.Tests/      xUnit, headless
godot/              Godot project (project.godot, Game.csproj -> ../src/Sim)
data/               unit and faction definitions (JSON)
docs/               design notes (story documents are kept out of the repo)
```

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

Unattended visual check: `-- --demo` runs a scripted attack, spill and repair at 3x. Add `--write-movie <dir>/f.png --fixed-fps 10 --quit-after 250` to capture frames.
