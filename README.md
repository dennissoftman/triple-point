# Triple Point

*Working title.* A prototype real-time strategy game in the spirit of C&C Generals, built around a **shared, physical conveyor-belt economy**: resources travel along belts across the map, and every package you take is one your enemy doesn't get. Belts break, spill and get repaired; switches steer the stream to whoever holds them.

Godot 4.7 (.NET) and C#. The whole game simulation is a plain C# library with no engine dependency, ticking at a fixed 20 Hz; Godot only draws it and turns input into commands.

**Status:** early prototype, no art. The current milestone asks one question: is fighting over the belt fun?

## What's in it

- Belts with breakable segments, spilled packages and repair; merges and capturable switches that split the stream while neutral.
- Construction: a builder puts up barracks, factories, gatherer posts beside the belt, and turrets, paying as they grow.
- Production with queues, repeat and rally points, paid as units build, so production speed follows belt income.
- A rifle squad, a scout car and a tank: data-driven weapons (bullets and shells, direct and splash), eased vehicle driving with turrets, firing on the move, return fire, attack-move.
- A point-symmetric two-player map, a minimap, hotseat play (F2), and a stress scene for performance.

## Build and run

Needs the [Godot 4.7.2 .NET build](https://godotengine.org/download) and the .NET 10 SDK.

```bash
dotnet build Game.sln
dotnet test Game.sln
```

Then open `godot/project.godot` in Godot and press Play, or run it from the command line:

```bash
godot --path godot
```

`CLAUDE.md` lists the other commands: the input smoke test, the stress scene and an unattended demo.

## Layout

```
src/Sim/         the simulation (no Godot references)
src/Sim.Tests/   xUnit tests for it
godot/           the Godot project: scenes, views, input, UI
data/            game data as JSON: units, weapons, buildings
docs/            design doctrine and architecture
tools/           input smoke test, map generator
```

- `docs/doctrine.md`: the design rules, each tagged built, decided, proposed or idea, plus what was rejected and why.
- `docs/architecture.md`: how the code is organized, measured performance and planned systems.
- `CLAUDE.md`: working rules and commands for AI-assisted development (much of this code is written with Claude Code).

All art is placeholder; see `godot/assets/PLACEHOLDERS.md`.

## License

The source code is licensed under the [Apache License 2.0](LICENSE); see also [NOTICE](NOTICE).

The license covers the code only. Art, audio, story and other creative content, and the game's name, are not licensed for reuse. Everything in the repo today is code, data and placeholder art.
