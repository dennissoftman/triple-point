# Design doctrine

What the game is and the rules it follows. Every rule carries a status:

- **[built]** in the code now; the code is the reference for details.
- **[decided]** agreed, not built yet.
- **[proposed]** a default to confirm in playtest.
- **[idea]** worth keeping, not agreed.

Rules, not numbers: tuning values live in `data/*.json` and the constants at the top of `src/Sim/Simulation.cs`. A number appears here only when it states a design relation (X outranges Y). When a decision changes in conversation, change it here in the same session, and flip tags as things get built.

## Pitch and pillars

A Command & Conquer Generals-style RTS, set around 2050-2100. Three asymmetric blocs fight for control of the economy.

- **A shared, physical belt economy.** Resources ("packages") travel along conveyor belts across the map. Every package you take is one your enemy doesn't get.
- **Automate the clicks, not the choices.** The player decides where and when; the game handles how. Every button is a decision, not upkeep.
- **Thinking over rushing.** Fights cost economy, map control matters, infrastructure breaks and gets repaired.
- **Campaign first**, grounded and morally complex. The story is Denys's alone.

References: C&C Generals / Zero Hour (factions, feel), Company of Heroes and Iron Harvest (readable units, grounded worlds), Frostpunk (desaturated mood), Titanfall 2 (teaching through level design), Tooth and Tail (accessible control).
Anti-references: Tempest Rising (muddy visuals, vague controls), Dawn of War squad-upgrade itemization, HoI4 micromanagement.

## MVP

**The one question: is fighting over the belt fun?** Anything that doesn't help answer it waits.

Milestones:

1. [built] Camera, selection, move orders.
2. [built] Belt, packages, gatherer posts, a debug overlay (HUD counters).
3. [built] Break, spill and repair segments; junction merges and switches. Not yet: jammed segments, roads.
3.5. [decided] Symmetric belt test: a mirror match with generic units, before asymmetry. This way a failed asymmetric test can be traced to the factions, not the belt.
4. Two factions (Asian vs Western), 2-3 units each, one of them an infantry squad. Groundwork [built]: two players, ownership, combat, a rifle squad, a scout car and a tank, switch capture, F2 hotseat. **Next:** spending Resources (production), then win/lose, then the AI.
5. [decided] A basic commander AI that fights over the belt.

After that, play against the AI with friends. If they ask for more, build a vertical slice: the first 2-3 missions of one faction.

**Not in the MVP:** the Eastern bloc, bridges, civilian unrest, multiplayer and lockstep, fog of war, normal maps, FSR, CMAA2, ECS, save/load, wear transitions, decals, damage stages (a color swap per state is enough).

## Belt economy

- [built] Belts are a fixed network of lines and junctions, authored per map. Players never build belt.
- [built] Lines are cubic Bezier curves, cut at load into short breakable segments. Packages move by arc length at constant speed and keep a minimum spacing. A queue that reaches a source blocks spawning, and those packages never exist. Lines fed by a junction don't spawn. Lines ending in one hand packages over; other lines lose packages at their end.
- [built] **Segment health.** Units shoot a segment to break it. A damaged segment still works. A broken one stays broken until repaired back to full health, by a unit standing next to it.
- [built] **A broken segment spills.** Packages on it, and every package that reaches it, fall beside it. A share is destroyed in the fall (spill loss, per belt), so holding a break never captures the whole stream. The rest become ground pickups: any unit walking over one collects it for its owner, and uncollected pickups fade. Downstream gets nothing until the repair.
- [built] **Gatherer posts** stand beside a belt with a pull point. An idle post grabs a passing package (+1 Resource for its owner), then works for a while; packages passing meanwhile go on. Upstream posts get first pick, and a post whose rate matches the flow starves everything below it.
- [built] **Merges:** inputs take turns into the output. An overloaded merge backs up all its inputs evenly.
- [built] **Switches steer the stream and never stop it.** A neutral switch splits packages evenly between its outputs.
  - **Capture:** a side takes a switch by holding it with squads, with no enemy inside the capture radius, for the capture time. More squads don't capture faster. Vehicles can't capture, but they do deny it. Both sides present freezes progress, and leaving drains it.
  - **On capture,** it turns to the captor's side: the first output whose stream reaches one of their posts. If none does, it keeps feeding what it fed.
  - **The owner** flips it between outputs from anywhere. The enemy has to recapture it first.
- [decided] Segment states include **jammed** (slowed).
- [proposed] Fighting jams only the segment it happens on, not the whole belt.
- [decided] **Resources:** one spendable currency, from packages. **Energy** arrives with buildings. It's a capacity, not a stockpile (C&C-style): power plants supply it, buildings draw it, and a deficit pauses power charge timers, shuts down power-hungry buildings and slows production. Power plants are raid targets off the belt.
- [idea] **Finite sources:** sources stop after N packages, forcing independent income or fights over what's left. Try it only after the belt is proven fun. Risk: if independent income matches contesting the belt, nobody fights over it.
- Known risks, to solve with map design and tuning: upstream advantage (answers: loops, multiple belts, reversible flow) and snowballing (answers: per-post caps, catch-up rules).

## Units, orders and combat

- [built] **Orders:** move, attack (a unit or post), attack-move, attack a segment, repair a segment. A move onto a switch captures it. Shift queues orders; a plain order replaces the queue. Orders only reach units their issuer owns.
- [decided] Belt-targeted orders still to come: guard segment, raid segment, escort convoy.
- [built] **Fire on the move.** Units fire at the weakest enemy in range whatever they're doing (fewest hit points left, nearest on ties; units before posts), so groups focus fire without clicks. They never stop or chase for it: a move arrives on time, and attack-move is the order that stops to fight. Attack chases into range and fires until the target dies. [decided] Heavy or emplaced weapons that must stop before firing become a per-type flag once such a unit exists.
- [built] **Return fire.** An idle or attack-moving unit hit by an enemy while nothing is in range to shoot back at chases the attacker into range and fires on it, and idle or attack-moving allies close by join in.
  - **Leash:** it gives up past a leash distance from where the chase began and ignores that attacker until it comes back within the leash. Idle units walk back to their spot; attack-move carries on to its point.
  - **Exceptions:** a move order is never diverted, and no other explicit order is either. Units inside a switch's capture radius hold it and only turn their turret to the attacker. Immobile and unarmed units don't answer.
  - After a hit, a unit with nothing in range keeps its turret on the attacker for a few seconds, the one exception to turrets ignoring enemies out of range.
  - Being outranged still loses: the answer is retreat automation (below), not return fire.
- [built] **Weapons are data.** Each unit type names a weapon. Damage comes as discrete shots with a reload, not a steady stream.
  - **Delivery:** a bullet hits at once and draws a tracer. A shell is a projectile that homes on its target and hits on arrival.
  - **Hit:** direct hits only the target. Splash hits every enemy within its radius, less toward the edge.
  - **Squads:** a squad's shot is every living member's damage. Under splash, a squad takes damage on the share of its footprint the blast covers, so bunched squads suffer.
  - No friendly fire yet; decide it with artillery.
  - **Assignments:** rifle squad bullets; scout car a light machine gun (bullets); tank cannon shells, **direct**.
  - [decided] Splash is for later artillery and rockets: they're the counter to infantry, and tanks stay anti-vehicle.
  - [decided] Armor classes with a damage-vs-armor table, and ground/air targeting (only anti-air weapons hit air), plug into the same weapon record. One weapon per unit until a unit needs two.
- [built] **Vehicles outrange infantry** (tank > scout car > squad), so vehicles hold switches and infantry takes them. Taking a switch held by vehicles needs anti-vehicle units.
- [built] **Squads:** fixed size per type, indivisible, one sim entity (one position, one order). Members are slices of one health pool and die one by one, each taking its share of damage with it.
  - Members are formation slots drawn by the view; they become sim state only if that looks wrong in play.
  - [decided] Squads reinforce near base or relay points, for a cost.
- [built] **Vehicles:** a single unit with movement per type (wheeled or tracked). They accelerate, brake and turn at limited rates.
  - Tracked vehicles pivot almost on the spot; wheeled ones need speed to steer, so they arc.
  - **Easing:** everything is eased (ease in, ease out), as physics that reacts to new orders mid-move, not as tween curves.
  - **Reversing:** a stopped vehicle backs up to a close target behind it.
  - **Turrets:** turrets are sim state. They turn at their own rate and fire only once on target. An attack order swings the turret onto its target while the vehicle drives there. Otherwise turrets only turn to enemies already in range, or to whoever just hit them.
  - **Lean:** the view leans the hull on its suspension (look only).
- [decided] **Production (MVP):** each player starts with one HQ, authored on the map, that trains every unit type. There's no construction yet.
  - **Cost:** each unit type has a cost and a build time. Cost is paid as it builds, tick by tick, and production stalls while its owner is broke, so production speed follows belt income directly.
  - **Queue:** one per HQ, first in first out, one unit at a time, with a short cap. Cancelling refunds what was paid.
  - **Repeat:** one toggle per HQ. Each finished unit goes back to the end of the queue, so a mix keeps its ratio.
  - **Rally point:** finished units leave the HQ and move to its rally point.
  - **HQ targeting:** the HQ is a target like a post. Destroying it stops that player's production; losing comes with `CanStillRecover`.
  - **Not yet:** Energy (it comes with several building types) and squad reinforcement.
- [decided] **Automation:** production repeats, gatherers self-manage, damaged units retreat, abilities are used sensibly. The player can always override. Upgrades are global or per unit type, never per squad. Most units have zero or one active ability.

## Controls

- [built] Right-click does the obvious thing:
  - attack an enemy
  - capture a switch
  - repair a damaged segment
  - otherwise, move
- [built] Ctrl+right-click a segment attacks it.
- [built] A then left-click attack-moves: left-clicking an enemy attacks it, Shift chains waypoints, and right-click or Esc cancels.
- [built] Left-click your own switch to flip it.
- [built] Double-click a unit to select every unit of its type on screen.
- [built] The cursor shows what a click will do, computed by the same code that issues the order. Round cursors click at their center; tool cursors point up-left, as on Windows.
- [built] The camera pans with the arrow keys (A is taken by attack-move), screen edges and middle-drag, and zooms with the wheel. It runs on real time, unaffected by game speed.
- [built] Game speed is 1x, 1.5x, 2x or 3x. The sim always runs 20 ticks per sim-second, so speed never changes results.
- [built] F2 hotseat: each side keeps its own camera. Swapping glides to that side's last view, or its spawn on its first visit.
- [decided] The camera is perspective, with pitch 55-60° and field of view 35-40°. Lock it before serious modeling.

## Factions (working titles; final names are Denys's)

- **"Asian" bloc: heavy, slow, sturdy.**
  - Heavily armored, powerful, slow units; structures build slowly but are very sturdy.
  - Builds and upgrades roads. Fastest repairs.
  - Construction: Generals-style dozers.
  - Gathering: heavy trucks, slow but big loads (high-value raid targets).
  - Belt: armored segments and fast repairs [decided in principle].
  - [idea] Artillery and area weapons as its counter to infantry.
- **"Western" bloc: fast, technological, expensive.**
  - Mostly aerial plus a basic ground army (like Generals' Air Force General). Anti-air silences it; it depends on money, with paid, limited special powers.
  - Construction: Red Alert-style influence zones from relay points.
  - Gathering: no posts; aerial carriers take packages from the belt to base, with a stealth upgrade.
  - Belt: drones pull packages mid-route and fly over breaks [decided in principle].
  - Rebuild fallback: see open decisions.
- **"Eastern" bloc: partisan, stealthy, opportunistic.** Added last, as a layer on the other two.
  - Taxes captured belt-side civilian buildings instead of using posts; must have base income in every matchup.
  - An intermediary between the other two blocs, leaning one way but never acting against itself.
  - Salvages wrecks for reuse or scrap. Captures and converts buildings, or peasants build cheap civilian-style structures, so razing never locks it out.
  - Belt: hidden taps and hijacked junctions [decided in principle].
  - Its taxes raise civilian annoyance.

## Infrastructure and construction

- [decided] One system: infrastructure objects with states (intact, damaged, broken) that can be repaired or improved. Belt segments are the first user [built]; roads and bridges reuse it.
- [decided] Roads come in two states, worn and improved, which set movement cost. Bridges only over impassable terrain, never over roads, so navigation stays single-layer.
- [decided] Engineers repair: it costs money and time, they're unarmed, auto-repair works in a limited radius, and the player sets priorities. Prototype repair is any unit, free.
- [decided] Buildings snap to the navigation grid, rotated in 90° steps.
- [proposed] Belt buildings (posts, relays) snap to sockets beside segments.
- **Map rule:** keep structures and spawns out of range of capture points, unless that's the point: at least the longest weapon range plus the capture radius. Otherwise whoever holds the point kills them for free.

## Win and lose (skirmish)

[decided] A player loses when they can no longer rebuild: **no buildings, and either no builders or not enough money for the cheapest building.** A 60 s grace timer runs while there are no buildings but recovery is still possible. Implement it as one function, `CanStillRecover(player)`, run every tick, backed by a unit-test table of edge cases:

- foundations and buildings under construction count as buildings
- captured civilian buildings count as buildings (Eastern)
- builders inside transports count
- cargo in transit does not count as money; refunds do
- free options count, such as a peasant capturing a free building
- income that needs no buildings counts
- the timer pauses while a foundation exists, and resumes (doesn't reset) when it's destroyed
- in team games, the rule applies to the whole team
- the Western rebuild fallback (open decisions)

## Campaign and unrest (post-MVP)

- [decided] Campaign first: one faction's campaign (6-8 missions), with skirmish vs AI built alongside.
  - Teach through situations, not tooltips.
  - One new mechanic per mission, introduced under pressure.
  - Tell the story mostly during play; cutscenes only for turning points.
  - One war seen from three sides.
  - Choices can change later maps.
- [decided] **Civilian unrest** (the campaign centerpiece):
  - Support is tracked per area and faction. Razing, street fighting and occupation lower it; protecting civilians raises it.
  - It drops fast and recovers slowly, through time and behavior only. **Never through money.**
  - Escalating stages: grumbling, theft from gatherers, belt sabotage, militia.
  - The Eastern bloc profits from others' unrest.
  - Razing must cost the razer something.

## Look

- [decided] The environment is worn, desaturated and mid-contrast. Units have neutral bodies with saturated faction accents: silhouette first, color second. The belt and packages are the highest contrast on screen.
- [decided] Scale is exaggerated: units are large relative to buildings and roads.
- [decided] Readability test: grayscale and squint, judged from the gameplay camera in a busy scene.
- [decided] Detail budget:
  - PS2-PS3 era: units about 1-5k triangles, heroes and big vehicles 10-15k.
  - One texture sharpness everywhere; a small shared material kit.
  - Stylized hand-painted color textures; no normal maps in the MVP.
  - One unit palette texture, with faction color as a per-instance shader parameter.
  - 2-3 building trim sheets, and 6-10 terrain textures.
- [decided] **Pipeline:**
  - Models: Blender to `.glb`, 1 unit = 1 m. Vehicles use rigid parts; infantry is skeletal, authored at 30 fps.
  - Textures: Material Maker first, then Krita; CC0 photo bases.
  - Before real models, a test diorama in Blender and then Godot, with the camera matched to the game's.
- Every placeholder or AI-generated asset goes in `godot/assets/PLACEHOLDERS.md`.

## Open decisions (defaults until changed)

1. **Grid and MVP map:** 2 m cells; MVP map 256 m square. Footprints: squad 1×1 cell, light vehicle 2×2, heavy 3×3. The prototype map (80×60 m) has no grid yet.
2. **Belt flow:** fixed-rate sources; packages reaching an end are lost. Loops are a map option.
3. **Western rebuild fallback:** a mobile relay unit, or an airdropped first structure.
4. **Friendly fire** for splash: decide with artillery.
5. **Fog of war:** post-MVP, grid-based.
6. **Baseline test machine:** undecided (Steam Deck or a mid-range laptop).
7. **Name:** working title *Triple Point*. Code names stay neutral (`Game.sln`, assembly `Game`).
8. **Tax and unrest balance, final faction names:** later, Denys's call.

## Rejected (don't re-propose without a new reason)

- **Two belt currencies:** they clutter the belt, the one thing that must read instantly, and double the cost tuning across three factions. High-value packages give the "which to raid" choice more cheaply.
- **Switches that close or pause the stream:** players steer the flow and build on it, they don't stop it.
- **Splash damage on tank shells:** tanks would be best at everything; splash belongs to artillery and rockets.
- **Turrets tracking enemies out of range, with no order:** too twitchy. Only an attack order aims ahead, and a hit makes the turret watch its attacker for a while.
- **Upfront payment for production (Generals style):** repeat would wait until the full cost was banked, and it hides how belt income becomes units.
- **Units that stop firing while moving:** units fire on the move; only a future per-type flag for heavy or emplaced weapons.
- **Tween curves for vehicle motion:** easing is physics, so it reacts to new orders mid-move.
- **Navmesh, TOML game data, TAA, ECS (for now):** reasons in `docs/architecture.md`.
