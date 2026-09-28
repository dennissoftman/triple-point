# Handoff: C&C-style RTS (working title TBD)

The repo's `CLAUDE.md` carries section 1 and the architecture rules and points here, so an assistant working in the repo picks this up automatically. For any other assistant, paste this document as the first message. It captures every decision made so far, what is still open, and the first setup steps.

---

## 1. Working rules (for the assistant)

- **Do not write story, lore, dialogue, mission narrative, character or faction names, or any other narrative content.** The story is written by the author alone, without AI. When story material is shared, critique it at the stage named (premise, outline, or written missions). Critique only, no rewrites.
- MVP first. Don't build engine tech nobody sees. If something isn't in the MVP scope (section 8), say so before building it.
- The simulation stays independent of Godot (section 5). Never add a Godot reference to the `Sim` project.
- Placeholder art may include AI-generated textures, but every such asset must be listed in `godot/assets/PLACEHOLDERS.md` so nothing ships by accident (Steam asks developers to disclose AI-generated content).

---

## 2. The pitch

A Command & Conquer-style RTS set around 2050-2100, a spiritual successor to C&C Generals in feel. Three asymmetric blocs fight for world domination and control of the economy. What makes it different:

- **A physical, shared conveyor-belt economy** running through the map. Resources ("packages", working term) travel along it. Players pull them off, and every package you take is one your enemy doesn't get.
- **Arcade-style control through heavy automation.** The player decides where and when; the game handles how.
- **Campaign first**, with grounded, morally complex storytelling: no cartoon good guys and bad guys.
- **Thinking over rushing.** Fights have an economic cost, map control matters, and infrastructure can be broken and repaired.

References: C&C Generals / Zero Hour (factions, feel), Company of Heroes and Iron Harvest (readable units in grounded worlds), Frostpunk (desaturated mood), Titanfall 2 (teaching through level design), Tooth and Tail (accessible RTS control).

Anti-references: Tempest Rising (muddy, low-contrast visuals, vague controls, no real link to C&C), Dawn of War factions built on squad upgrades and item activations, HoI4-style micromanagement.

---

## 3. Design decisions (closed unless marked)

### 3.1 Belt economy

- One or more conveyor belts cross the map as a fixed network of **segments** and **junctions**. Packages move along segments.
- Players collect packages with faction-specific gatherers (3.2). Collected packages leave the belt. More or upgraded gatherers collect faster.
- Segment states: **normal**, **jammed** (slowed), **broken** (stopped until repaired).
- Segments are cubic Bezier curves; packages move by arc length at constant speed. Maps author belts as Godot `Path3D` curves, converted to plain sim data at load.
- Authored curves are cut into short breakable segments at load (default max 5 m, a tuning value), so map authors draw long smooth curves and breakability stays fine-grained.
- Packages keep a minimum spacing and queue when held; a queue that reaches the source blocks spawns (those packages never exist).
- **Segments have health.** Units break a segment by shooting it from range (default 100 health, 10 dps: 10 s for one unit). A damaged segment keeps working. At 0 it breaks, and it stays broken until repaired back to full health (5 s for one unit from 0).
- **A broken segment spills.** Packages on it when it breaks, and every package that reaches it while broken, fall off beside it. A share of them is destroyed in the fall (spill loss, default 30%, tunable per belt); the rest become ground pickups. Any unit walking over a pickup collects it; uncollected pickups fade after 60 s. So a break redirects most of the flow to whoever holds the spot, its value grows the longer it's held, and downstream gets nothing until repair.
- *Tuning note:* one repairer currently out-heals one attacker (20 health/s vs 10 dps). Fine while there's no combat between units; revisit with real unit types.
- **Gatherer posts** (generic for now; faction gatherers later) stand beside a belt with a pull point on it. An idle post grabs a package passing its pull point, adds 1 Resource, and works for 2 s; packages passing meanwhile go on downstream. So upstream posts get first pick, and a post whose rate matches the flow starves everything below it.
- **Junctions** are where lines meet; lines attach by their endpoints. Every input feeds the selected output. Several inputs make a merge (inputs take turns, one package per tick at most, only when the output's entry is clear, so an overloaded merge backs up both inputs evenly). Several outputs make a switch. **Switches are captured, not flipped by presence:** a switch starts neutral and closed (its inputs back up); a side takes it by holding it with units and no enemy within 4 m for 5 s (both sides present freezes progress; leaving drains it). The owner sets the live output from anywhere; the enemy has to recapture it first. Shown as a ring at the capture radius that fills with the capturer's color. Lines fed by a junction don't spawn; lines ending in one hand packages off instead of losing them. Breaking the segment just past a merge spills every input at once.
- Players can break segments and switch junctions to reroute flow. The belt layout itself is fixed by map design; players don't build new belt.
- **Fighting jams only the segment it happens on** (a local slowdown), rather than pausing the whole belt. *Proposed, not yet confirmed; validate in playtest.*
- Known risks to solve through map design and tuning: upstream advantage (loops, multiple belts, flow that can change direction) and snowballing (caps per gathering point, or catch-up rules).

### 3.2 Factions (working titles only; final names will be invented blocs, not real regions)

**"Asian" bloc: heavy, slow, sturdy**
- Heavily armored, powerful, slow units. Structures build slowly but are very sturdy.
- Builds and upgrades roads for faster movement. Fastest repairs.
- Construction: Generals-style dozers build on site.
- Gathering: heavy trucks and machines, slow but carrying big loads (high-value raid targets).
- Belt interaction (agreed in principle): armored belt segments and fast repairs, so their lines are hard to cut.
- *Idea, not confirmed:* artillery and area weapons as their natural counter to infantry squads.

**"Western" bloc: fast, technological, expensive**
- Fast, high-tech, pricey. Mostly aerial, plus a basic ground army (like Generals' Air Force General).
- Weakness: AA can silence their air power, and they depend on money. Paid, limited special powers. Economic vulnerability is a weakness, not an automatic loss.
- Gathering: no gatherer posts. Aerial units (stealth upgrade available) carry packages from the belt to base, protected by relay points or turrets.
- Belt interaction (agreed in principle): drones can pull packages mid-route and fly over broken segments; flexible, but vulnerable while carrying cargo.
- Construction: Red Alert-style, building within an influence zone projected by relay points. Destroying relays shrinks where they can build and supply.
- Rebuild fallback when they have no buildings is still open (section 7).

**"Eastern" bloc: partisan, stealthy, opportunistic ("silent but deadly, like the wind")**
- Captures civilian buildings near belt routes and taxes them instead of using gatherer posts. Acts as an intermediary between the other two blocs, leaning toward one side or the other but never acting against itself (for now).
- Must have base income in every matchup (1v1, mirror match, vs AI), from belt-side civilian buildings. Profiting from the other blocs is a bonus on top.
- Salvages wrecked vehicles: repairs and modifies them for reuse, or sells them as scrap. Provisions captured buildings as barracks or arms dealers.
- Construction: captures and converts civilian buildings, or peasants build new civilian-style structures (cheap, weak, low-profile) that can then be converted. Razing every civilian building therefore never locks them out.
- Reinforces squads from captured civilian buildings.
- Belt interaction (agreed in principle): hidden taps that quietly siphon packages, and hijacked junctions.
- Their taxes raise civilian annoyance, even in their own areas (balance later, see 3.6).
- Added to the prototype last, because it's designed as a layer on top of the other two.

### 3.3 Units and control

- **Automate the clicks, not the choices.** Production repeats, gatherers self-manage, damaged units retreat, abilities are used sensibly. The player can always override.
- Orders can target pieces of the belt: guard segment, raid segment, escort convoy, take junction.
- Shift-click queues orders; a plain order replaces the queue. Queued orders are drawn as a path on the ground.
- Every button must be a decision, not upkeep. Upgrades are global or per unit type, never per squad. Most units have zero or one active ability.
- **Infantry fights in squads.** Fixed size per type (e.g. 5 riflemen, 3 engineers), indivisible (no splitting). One simulation entity per squad: one path, one order, one grid footprint. Members are visual formation slots with local steering and individual HP. Damage is distributed across members. Area damage uses a simple rule against the squad's area (e.g. the fraction of members hit follows the overlap between blast and squad footprint), not exact member positions. Member positions become sim state only if that looks wrong in play. Squads reinforce near base or relay points, which costs resources.
- **Vehicles are single units.**
- *Prototype squads (built):* a squad's members are slices of one health pool. They die one by one as it drops, each taking its share of the squad's damage with it, so a worn squad fights weaker. Drawn as capsules walking loosely in a wedge; vehicles are boxes with a turret. Unit types live in `data/units.json`.
- **Ownership and combat (prototype, built):** players own units and gatherer posts; belts stay neutral. Orders carry their issuer and only reach that player's units. Posts pay their owner; pickups pay whoever's unit collects them. Right-click an enemy unit or post to attack it (chase into range, fire until destroyed). Idle units fire at the nearest enemy in range, units before posts, without chasing; units with orders don't stop to shoot ("move means move"). Destroyed units and posts are removed. Health shows as a bar when damaged or selected.
- *Map lesson:* posts within weapon range of the contested switch die to whoever holds it. Keep structures out of range of capture points unless that's the intent.

### 3.4 Infrastructure: roads, bridges, repair

- One system: infrastructure objects with states (intact / damaged / broken) that can be repaired or improved. Belt segments are its first use; bridges and roads reuse it.
- Roads: worn vs improved (two states in the MVP), which changes movement cost.
- Bridges: only over impassable terrain (water, chasms), never over roads, so navigation stays single-layer.
- Engineers repair. Repair costs money and takes time; engineers are unarmed and vulnerable; auto-repair only works within a limited radius; the player sets repair priorities. The Asian bloc repairs fastest.

### 3.5 Construction and placement

- Buildings snap to the navigation grid, with rotation in 90-degree steps. No free placement.
- Construction method differs per faction (3.2): Asian dozers, Western influence zones, Eastern capture and peasants.
- *Proposed, not yet confirmed:* belt-related buildings (gatherer posts, relays) snap to predefined sockets beside belt segments.

### 3.6 Civilian unrest (post-MVP; campaign centerpiece)

- Each area tracks support per faction. Razing ordinary civilian buildings, fighting in streets and occupying buildings lower it. Leaving civilians alone and protecting them raise it.
- Support drops fast and recovers slowly, through time and behavior only. **Never through paying money** (pay-to-forgive is an exploit).
- Visible, escalating stages: grumbling, then theft from nearby gatherers, then belt sabotage, then armed militia attacking the occupier.
- The Eastern bloc gains from others' unrest: recruits, cheaper conversions, locals joining.
- Eastern taxes raise annoyance too. An area's support could set how much tax it tolerates.
- Razing civilians must cost the razer something (e.g. rubble the Eastern bloc can salvage, fairly sturdy civilian buildings).

### 3.7 Win and lose (skirmish; campaign missions define their own conditions)

- A player loses when they can no longer rebuild: **no buildings AND (no builders OR not enough money for the cheapest building)**.
- A builder plus enough money means not lost. Builders with no money means lost.
- **60-second grace timer** when there are no buildings but recovery is still possible.
- Implement as one function evaluated every tick, `CanStillRecover(player)`, backed by a unit-test table of edge cases:
  - Foundations and buildings under construction count as buildings.
  - Captured civilian buildings count as buildings for the Eastern bloc.
  - Builders inside transports or garrisons count.
  - Cargo in transit does not count as money (it can't be delivered without buildings).
  - Refunds from cancelling construction count as money.
  - Free options count (an Eastern peasant can capture a free civilian building while one still exists).
  - Income that works without buildings (e.g. instant-credit salvage) counts.
  - The timer starts when there are no buildings but recovery is possible, pauses while a foundation exists, and resumes (not resets) if that foundation is destroyed, so foundation spam can't stall forever.
  - In team games the rule applies to the whole team.
  - The Western bloc needs a rebuild fallback (section 7).

### 3.8 Campaign

- Campaign first. Start with one faction's campaign (about 6-8 missions), with skirmish vs AI built alongside.
- Principles:
  - Teach through situations, not tooltips.
  - One new mechanic per mission, introduced under pressure, combined with earlier ones later.
  - Tell the story mostly during play (radio chatter, intercepted transmissions, changes in the world). Cutscenes only for turning points.
  - One war shown from three sides, each side's reasons making sense from the inside.
  - Choices can change later maps.
- The story itself is Denys's work only (section 1).

---

## 4. Visual direction and art pipeline

### 4.1 Look

- Worn, desaturated, mid-contrast environment: the narration layer.
- Units: neutral bodies with clean, saturated faction accents on large flat areas. Identity comes from silhouette first, color second.
- Belt and packages: the highest contrast on screen, possibly slightly emissive.
- Readability tests: grayscale plus squint test. Judge everything from the gameplay camera in a busy scene, never only up close in an editor.
- Scale is deliberately exaggerated: units larger relative to buildings, roads and bridges than real life.
- Possible palette shift across the campaign (the writer's call).

### 4.2 Detail budget

- PS2-PS3 era: regular units about 1-5k triangles; hero units and big vehicles up to about 10-15k; buildings whatever gives a clear, recognizable shape.
- Consistency comes from one texture sharpness (pixels per meter) everywhere and a small shared material kit, more than from polygon counts.

### 4.3 Textures

- Stylized, hand-painted-style color textures with wear, grime and edge highlights painted in, and simple material values (mostly high roughness, metallic only on bare metal). Dynamic lighting handles light and shadow; no shadows painted into textures.
- Filtered (not pixelated), fairly low-res.
- No normal maps in the MVP. Baked ambient occlusion is optional.
- Units: one shared palette texture. Faction colors via per-instance shader parameters (`instance uniform`), so one material serves all factions.
- Buildings: 2-3 trim sheets.
- Terrain: 6-10 tiling textures (grass, dirt, worn road, improved road, concrete, rock, rubble).
- About 10-15 textures total for the MVP.
- Tools: Material Maker (primary; procedural, tiles automatically), Krita (wraparound mode, W key), CC0 photo textures from Poly Haven or ambientCG as a base for stylizing, ArmorPaint for painting directly on models if needed. AI placeholder textures allowed if tracked in `PLACEHOLDERS.md`.

### 4.4 Models and animation

- Blender to glTF binary (`.glb`). 1 unit = 1 meter everywhere. Apply transforms before export.
- Vehicles: rigid-part animation (turret rotation, wheel spin, tread texture scroll), no skeletons.
- Infantry: skeletal. Keyframes authored at 30 fps (the engine interpolates). Distant units update animation less often. Vertex animation textures only if crowds get large (much later).

### 4.5 Test diorama (before real models)

- Contents: one belt segment with a junction, a road in both states, a bridge, a few civilian buildings, 2-3 units per faction, a grid plane at navigation cell size, and a 1.8 m human for scale reference.
- Match the Blender camera to the planned game camera (angle, distance, field of view, perspective vs orthographic). Render with Eevee, one sun light, AgX view transform. Render at 1080p and judge full screen.
- Try 2-3 style variants side by side.
- Rebuild the diorama in Godot as the final check.
- Keep a PureRef reference board sorted by category (silhouettes, palettes, wear, buildings).

---

## 5. Tech stack and architecture

### 5.1 Versions

- Godot 4.7.2, .NET build. C# on the .NET 10 SDK; all projects target `net10.0`.
- Desktop C# runs on the modern .NET runtime (CoreCLR) with a JIT, including tiered compilation and dynamic PGO. No C# web export (not needed for a PC RTS).

### 5.2 Core architecture

- **`Sim`** is a plain C# class library with zero Godot references: units, squads, grid, belt, economy, orders, AI, rules. Testable headless.
- Each tick is a function: `(state, commands) -> (newState, events)`.
  - **Commands** are order objects (move, attack, guard segment, raid segment, escort, repair, build...). Player input and AI issue the same orders.
  - **Events** are what happened this tick (unit died, segment broke, building finished), consumed by the presentation layer for effects, sound and UI.
- Fixed tick at **20 Hz**, driven by our own accumulator in `_Process` (not `_PhysicsProcess`). Views interpolate between ticks.
- **Game speed** (default 1x, presets 1x / 1.5x / 2x / 3x, any value up to 3x) scales sim time per real second in the host. The sim always ticks at 20 Hz of sim time, so speed never changes simulation results.
- No gameplay physics. Units move in the simulation; physics is for visual effects at most.
- Determinism isn't required now, but don't rule it out: one seeded RNG owned by the simulation, and all math routed through one place so fixed-point math (e.g. FixedMath.Net) can be swapped in later for lockstep multiplayer.
- `System.Numerics` (or our own types) inside `Sim`. Conversion to Godot types happens in one place at the boundary.
- Game data (unit stats, costs, faction rules) in JSON under `/data`, loaded by `Sim` with `System.Text.Json`. Not Godot Resources. JSON over TOML: game data nests (units with weapon lists, factions with unit lists), which TOML handles awkwardly; it needs no dependency; and the loader allows comments and trailing commas. TOML stays an option for flat settings files.

### 5.3 Presentation (Godot side)

- A `SimHost` node owns the simulation: drains input into commands, ticks, dispatches events, syncs views.
- A view registry maps entity ID to visual. Views only read simulation state (`Apply(snapshot, alpha)`) and never change it. No game logic in per-node `_Process`.
- MVP: one scene instance per unit is fine. Numerous unit types switch to `MultiMesh` later (and direct `RenderingServer` instances for very large counts) behind the same interface.
- Biggest performance trap: calls between C# and the engine cost far more than calls within C#. Batch across the boundary (one buffer push per frame for many transforms, not per-unit node calls).
- Selection is local UI state, not simulation state.
- C# conventions in Godot: script classes are `partial`, class name matches file name, prefer `[Export]` fields set in the inspector over `GetNode("path")` strings.

```csharp
public partial class SimHost : Node
{
    const double TickSeconds = 1.0 / 20.0;
    Simulation _sim;
    double _accumulator;

    public override void _Ready() => _sim = Simulation.Load("res://data/");

    public override void _Process(double delta)
    {
        _accumulator += delta;
        while (_accumulator >= TickSeconds)
        {
            var events = _sim.Tick(InputQueue.Drain());
            EventRouter.Dispatch(events);   // effects, sounds, UI
            _accumulator -= TickSeconds;
        }
        float alpha = (float)(_accumulator / TickSeconds);
        Views.Sync(_sim.State, alpha);      // smooth movement between ticks
    }
}
```

(`Simulation.Load` will need a real filesystem path or a globalized one, since `Sim` can't use Godot's `res://` API; resolve it on the Godot side and pass it in.)

### 5.4 Navigation (inside `Sim`, not Godot's navigation)

- Grid of cells (default 2 m, see section 7). Each cell stores movement cost (terrain, road quality), a blocked flag (buildings, broken bridges, water) and a clearance value (free space around it).
- A* with 8 directions plus path smoothing, or Theta* for any-angle paths.
- Clearance per cell so big vehicles don't path through gaps meant for infantry.
- Lazy re-pathing: when cells change, units re-path only when their next waypoint becomes blocked or more expensive.
- Flow fields for group moves to the same destination.
- Hierarchical A* only if long paths get slow (unlikely at this scale).
- Local avoidance: separation steering first; RVO2-CS if crowds jam.
- Air units ignore the grid.
- **No navmesh.** Reasons: constant runtime changes, multiple unit sizes, per-cell road costs, the commander AI's threat and influence maps need a grid anyway, flow fields need a grid, building placement snaps to cells, and grids are easier to keep deterministic and to debug.
- Optional libraries: Roy-T.AStar (grid A*), RVO2-CS (avoidance), DotRecast (only if a navmesh ever comes back).

### 5.5 AI (inside `Sim`)

- **Commander AI:** utility scoring (each option gets a score, pick the best) for economy, attacks and belt-defense priorities, using threat and influence maps on the grid.
- **Squad and unit AI:** small state machines or a minimal hand-written behavior tree.
- Both issue the same order objects as the player.
- LimboAI (has C# documentation) is optional, only for campaign scripting on the Godot side.

### 5.6 Performance habits in `Sim`

- Struct arrays per unit type, `Span<T>`, `ArrayPool` for temporary buffers, no LINQ or allocations inside the tick, `System.Numerics` SIMD where it helps, `Parallel.For` for independent work (pathfinding requests, visibility).
- No ECS for now. If organization or performance starts hurting: Arch or Friflo.Engine.ECS.

### 5.7 Rendering settings

- Forward+ renderer. AgX (or Filmic) tonemapping. Color grading through the Environment's color-correction texture.
- One strong directional light, with shadows tuned for the fixed RTS camera range (fewer cascades over a shorter distance gives sharper shadows).
- SSAO on. SSIL optional later.
- Anti-aliasing: SMAA (built in since Godot 4.5). Add MSAA 2x on top if unit silhouettes still crawl while panning. No TAA (it smears). Expose AA as a setting.
- Skip: real-time GI (SDFGI, VoxelGI), baked lightmaps (the world is destructible), ray tracing, screen-space reflections (unless there's lots of water).
- FSR 2 later, as an option for weak hardware. No official DLSS in Godot.
- Light post-processing only: no heavy bloom, grain, depth of field or chromatic aberration.
- Subtle outline or rim light only on selected or hovered units.
- **CMAA2: deferred side project.** Approach: render the game into a `SubViewport` with tonemapping on, run the CMAA2 compute passes through `RenderingDevice` from C#, and display the result through a `Texture2DRD` (a `CompositorEffect` runs before tonemapping, which is the wrong point). Check the license of Intel's reference repo first. Possibly release as an open-source Godot addon.

### 5.8 Tooling

- Git + Git LFS. Commit Godot's `.uid` files. Ignore `.godot/`, `bin/`, `obj/`.
- Debug overlay from day one: income per faction, flow rate per belt segment, jammed and broken segments, grid and clearance view.
- Tests: xUnit in `Sim.Tests`, headless. The win/lose edge-case table lives here.
- Test regularly on a baseline machine (section 7).

---

## 6. Project layout

```
/triple-point/
  CLAUDE.md           assistant rules (section 1) + architecture rules
  Game.sln            Godot uses this via dotnet/project/solution_directory = "res://.."
  src/Sim/            class library, net10.0, NO Godot reference
  src/Sim.Tests/      xUnit, references Sim
  godot/              Godot project
    project.godot
    Game.csproj       ProjectReference -> ../src/Sim/Sim.csproj
    scenes/           main.tscn, ui/, debug/
    views/            unit and building scenes + tiny view scripts
    scripts/          SimHost, input, camera
    assets/           models, textures, materials, PLACEHOLDERS.md
  data/               unit and faction definitions (JSON)
  docs/               design notes (story documents are kept out of the repo)
```

---

## 7. Open decisions (defaults to use until changed)

1. **Camera:** perspective, pitch around 55-60 degrees, field of view around 35-40 degrees, zoom range to settle in the diorama. Lock before serious modeling. *Prototype:* 55° pitch, 38° FOV, 15-60 m zoom, no rotation; pans with WASD/arrows, screen edges and middle-mouse drag; runs on real time, unaffected by game speed. Each side keeps its own view: swapping sides glides (0.6 s, eased) to that side's last view, or its spawn on its first turn. `CameraView` (focus + zoom) and `FlyTo` are the building blocks for replay cameras.
2. **Grid and map:** 2 m cells; prototype map 128 x 128 cells (256 m square). Footprints: infantry squad 1x1 (members spread visually), light vehicle 2x2, heavy Asian vehicle 3x3. Buildings occupy whole cells.
3. **Belt flow:** packages spawn at source nodes at a fixed rate; packages reaching an end are lost. Loop layouts as a map option.
4. **Resources (decided):** one spendable currency, from belt packages (plus slow independent generators if sources become finite). The MVP uses only this.
   **Energy (decided, arrives with buildings in milestone 4):** a capacity, not a stockpile, as in C&C. Power structures supply it and buildings draw it. A deficit pauses power charge timers, shuts down power-hungry buildings and slows production. Power plants are raid targets off the belt.
   Rejected: two belt currencies. They clutter the belt, the one thing that must read instantly, and double cost tuning across three asymmetric factions. High-value packages on the same belt give the "which one to raid" decision more cheaply.
4b. **Finite sources (experiment, after the belt is proven fun):** sources stop after a set number of packages, so players must build independent income or fight over scarce sources. Risk: if independent income is as good as contesting the belt, nobody fights over the belt, which removes the core loop.
5. **Combat model:** HP, armor class, a damage-vs-armor multiplier table, range, cooldown, and ground/air targeting flags. Only AA-flagged weapons can hit air units.
6. **Western rebuild fallback:** a mobile relay unit that projects a small influence zone, or airdropping the first structure anywhere. Undecided.
7. **Belt fight rule:** local segment jam (3.1). Confirm in playtest.
8. **Belt sockets** for gatherer posts and relays (3.5). Confirm.
9. **Fog of war:** post-MVP, grid-based visibility.
10. **Baseline test machine:** undecided (e.g. Steam Deck or a mid-range laptop).
11. **Project and repo name:** working title *Triple Point*. Code names stay neutral (`Game.sln`, `Game.csproj`, assembly `Game`) so a rename doesn't touch them.
12. **Later:** tax and unrest balance, final faction names and identities (Denys's call).

---

## 8. MVP scope

**Goal: answer one question. Is fighting over the belt fun?**

Milestones:
0. Setup: solution and projects build, Godot references `Sim`, one passing test, and a cube that moves because the simulation says so.
1. Camera, box selection, move orders. *Done:* camera pan/zoom, click and box selection, Shift add/toggle, group moves in a grid formation.
2. Belt with packages flowing, gatherers pulling from it, debug overlay. *Done* (gatherer posts are authored in the scene until construction exists; the overlay is the HUD counters).
3. Infrastructure states: break, jam and repair segments; junction switches; two road states. *Done:* segment health, break, spill, repair, junction merges and switches. *Not yet:* jammed state (waits for unit-vs-unit fighting), roads.
3.5. Symmetric belt test: a mirror match with generic units, to check the belt is fun before asymmetry is layered on. Western drones and Asian armored segments both blunt belt disruption, so a failed asymmetric test alone wouldn't say why.
4. Two factions (Asian vs Western), 2-3 units each, including one infantry squad type. *Groundwork done:* two players with ownership, unit-vs-unit and unit-vs-post combat, a generic rifle squad and vehicle, switch capture, and an F2 hotseat swap for testing both sides before the AI. *Next:* spending Resources (production), then win/lose, then the AI.
5. A basic commander AI that fights over the belt.

Then play it with friends, against the AI. That's enough for the MVP; no multiplayer needed. If they ask to play again, build the vertical slice: the first 2-3 campaign missions of one faction.

Parallel art track: lock the camera, then Blender diorama, then Godot diorama, then palette texture, one trim sheet and the terrain set in Material Maker. Greybox visuals with strong colors are fine until then.

**Not in the MVP:** Eastern bloc (added once the belt is proven), bridges (cheap once segments work), civilian unrest, multiplayer and lockstep, fog of war, normal maps, FSR, CMAA2, ECS, save/load, visual wear transitions, decals and damage stages (a simple model or color swap per state is enough).

---

## 9. First-session checklist (new machine)

1. Install: .NET 10 SDK (64-bit), Godot 4.7.2 .NET build, Git and Git LFS, Blender, Material Maker, Krita (optional), PureRef (optional).
2. Create the repo and C# projects:

```bash
git init <repo> && cd <repo>
git lfs install
dotnet new sln -n Game --format sln    # .NET 10 may default to .slnx; drop the flag if your SDK rejects it
dotnet new classlib -n Sim -o src/Sim
dotnet new xunit -n Sim.Tests -o src/Sim.Tests
dotnet sln add src/Sim/Sim.csproj src/Sim.Tests/Sim.Tests.csproj
dotnet add src/Sim.Tests/Sim.Tests.csproj reference src/Sim/Sim.csproj
```

3. Create the Godot project in `godot/` (Forward+ renderer; let it generate Git metadata). Create the first C# script so Godot generates `Game.csproj`, then:
   - set `<TargetFramework>net10.0</TargetFramework>`,
   - add `<ProjectReference Include="../src/Sim/Sim.csproj" />`,
   - add `godot/Game.csproj` to the root solution,
   - point Godot at the root solution via its `dotnet/project/solution_directory` project setting (or keep Godot's generated solution),
   - confirm it builds from both the Godot editor and `dotnet build`.
4. Git config:
   - `.gitattributes` with LFS for `*.glb`, `*.blend`, `*.png`, `*.jpg`, `*.exr`, `*.wav`, `*.ogg`, `*.kra`.
   - `.gitignore` with `.godot/`, `bin/`, `obj/`, IDE folders. Commit `.uid` files.
5. Rendering setup: AgX tonemapping, SMAA, SSAO, one `DirectionalLight3D`.
6. First code:
   - `Sim`: empty grid, one unit moving toward a target each tick, one xUnit test for it.
   - Godot: `SimHost`, a camera, a ground plane, and a cube synced from the simulation.

When the cube moves because the simulation says so, the architecture is proven end to end.

---

## 10. References

- **OpenRA:** open-source C# reimplementation of the classic C&C games, grid-based. Primary code reference.
- **Original C&C source code**, including Generals and Zero Hour, released by EA under GPL in 2025.
- **Game AI Pro** (free at gameaipro.com): Elijah Emerson's flow-field pathfinding chapter (Supreme Commander 2), plus the RTS commander AI and utility-scoring chapters.
- **"1500 Archers on a 28.8"** by Bettner and Terrano: Age of Empires lockstep networking.
- **Dustin Browder's GDC talk on StarCraft II's design**; GDC's YouTube channel for RTS talks and postmortems.
- **Books:** *Designing Games* (Tynan Sylvester), *The Art of Game Design* (Jesse Schell), *Game Feel* (Steve Swink), *Level Up!* (Scott Rogers), *Game Programming Patterns* (Robert Nystrom, free online), *Blood, Sweat, and Pixels* (Jason Schreier).
- **Video:** Game Maker's Toolkit; Blender Guru (donut series), Grant Abbitt, Josh Gambrell, CG Cookie; RodZilla (Material Maker).
