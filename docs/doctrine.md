# Design doctrine

What the game is and the rules it follows. Every rule carries a status:

- **[built]** in the code now; the code is the reference for details.
- **[decided]** agreed, not built yet.
- **[proposed]** a default to confirm in playtest.
- **[idea]** worth keeping, not agreed.

Rules, not numbers: tuning values live in `data/*.json` and the constants at the top of `src/Sim/Simulation.cs`. A number appears here only when it states a design relation (X outranges Y). When a decision changes in conversation, change it here in the same session, and flip tags as things get built.

## Pitch and pillars

A Command & Conquer Generals-style RTS, set around 2050-2100. Three asymmetric blocs fight for control of the economy.

- **A shared, physical belt economy.** Packages travel along conveyor belts across the map, and they're the one currency. Every package you take is one your enemy doesn't get.
- **Automate the clicks, not the choices.** The player decides where and when; the game handles how. Every button is a decision, not upkeep.
- **Thinking over rushing.** Fights cost economy, map control matters, infrastructure breaks and gets repaired.
- **Campaign first**, grounded and morally complex. The story is Denys's alone.

References: C&C Generals / Zero Hour (factions, feel), Company of Heroes and Iron Harvest (readable units, grounded worlds), Frostpunk (desaturated mood), Titanfall 2 (teaching through level design), Tooth and Tail (accessible control).
Anti-references: Tempest Rising (muddy visuals, vague controls), Dawn of War squad-upgrade itemization, HoI4 micromanagement.

## MVP

**The one question: is fighting over the belt fun?** Anything that doesn't help answer it waits.

Milestones:

1. [built] Camera, selection, move orders.
2. [built] Belt, packages, gatherer posts, a debug overlay (HUD counters, under F3).
3. [built] Break, spill and repair segments. (Merges and switches were built, then removed: see Rejected.) Not yet: jammed segments, roads.
3.5. [decided] Symmetric belt test: a mirror match with generic units, before asymmetry. This way a failed asymmetric test can be traced to the factions, not the belt.
4. Two factions (Asian vs Western), 2-3 units each, one of them an infantry squad. Groundwork [built]: two players, ownership, combat with return fire, a rifle squad, a scout car and a tank, production, a construction prototype (builder, barracks, factory, post, turret), a minimap, F2 hotseat, win/lose, navigation and fog of war (see Units: Navigation, Fog of war), built after the AI so there was an opponent to test them against.
5. [built] **A basic commander AI that fights over the belt** (v1, scripted), playing Red on the main map:
   - Economy: two builders; posts on its own half, nearest home first, never where enemy fighters stand; a barracks, then a factory; more posts while money allows. It saves for a building it wants instead of training.
   - Army: one engineer once it has a post, then whichever fighter it has fewest of (artillery counts double). Rally at a staging point in front of home.
   - Fighting: it defends anything of its own under attack; with 8 fighters and 1.5× the enemy's strength it pushes on the nearest enemy building; with 4 it raids the enemy's most exposed post, and sends one unit (artillery first) to break the enemy belt just upstream of their posts, but never belt that feeds its own. Units below 30% health pull back home, unless fewer than four healthy fighters are left: then everyone fights (nothing heals yet).
   - Upkeep: repairers mend broken belt feeding its posts when no enemy is near; idle units pick up spilled packages that no enemy stands near.
   - Scouting: when part of the enemy's belt hasn't been seen for 45 s, its fastest fighter (never artillery) goes to look.
   - It knows only what its side sees and remembers, through one knowledge layer (`AiView`): the same fog as a player.
   - [decided] **The AI after v1 is a behaviour tree** that decides for itself. v1 is scripted to prove the belt fight first. Its steps (survey, build, produce, fight, mend, collect, scout) are the future tree's subtrees, and `AiView` is its blackboard.

After that, play against the AI with friends. If they ask for more, build a vertical slice: the first 2-3 missions of one faction.

**Not in the MVP:** the Eastern bloc, bridges, civilian unrest, multiplayer and lockstep, normal maps, FSR, CMAA2, ECS, save/load, wear transitions, decals, damage stages (a color swap per state is enough).

## Belt economy

- [built] Belts are fixed lines, authored per map, each running from a source to an end, with no junctions (see Rejected). Players never build belt.
- [built] Lines are cubic Bezier curves, cut at load into short breakable segments. Packages move by arc length at constant speed and keep a minimum spacing. A queue that reaches a source blocks spawning, and those packages never exist.
- [built] **Sources are finite, and the belt returns what nobody takes.** Each source holds a reserve (set per map). Packages reaching a line's end go back into its reserve through the covered stretch beyond the map, so the only ways supply leaves the pool are posts taking it and spills: a break is the one waste, which is why it's worth repairing, and raiding burns supply both sides share. There's no loop on the map (see Rejected: junctions); the return is off the map. A gauge on the covered stretch where the belt comes into play shows what's left.
- [built] **Segment health.** Units shoot a segment to break it when ordered to (Ctrl+right-click): nothing picks a belt as its target on its own. Splash is the exception: it hurts any open segment in its radius, whoever's belt it is, so artillery fighting beside a belt breaks it by accident. A damaged segment still works. A broken one stays broken until repaired back to full health (Repair).
- [built] **Belts come from beyond the map and leave it again.** Where a belt runs outside the playable area, and through its owner's back field to where the open belt starts, it's **covered**: it can't be shot or broken (nobody could reach it to repair it) and takes no posts (so the back field adds no safe post slots). Maps set each line's covered start and end.
- [built] **Belts stand on legs,** high enough that every unit drives under them, so a belt never blocks movement and navigation needs no crossings. Posts reach up to it with a mast and an arm; spills fall to the ground beside it.
- [built] **A belt reads as a machine made of breakable pieces:** raised rails, a clamp over the rails at every joint between segments (flush across the surface, so packages pass over it), a surface that moves with the packages and shows which way the belt runs, a health bar over a damaged one, and a broken one sagging in two halves between its legs with debris fallen below, settling back as it's repaired. Covered stretches are a closed housing. With units selected, the segment under the cursor lights up in the color of what a right-click would do, and a hint beside the cursor gives its health and the keys.
- [built] **A broken segment spills, up to a pile.** Packages on it, and every package that reaches it, fall onto a pile of spots beside it that never overlap. Once the pile is full, packages wait at the break and the belt behind it stops; a finite source then keeps what it can't send, so a break wastes only what breaks in the fall. Collecting from the pile lets the belt move again. A share is destroyed in the fall (spill loss, per belt), so holding a break never captures the whole stream. The rest become ground pickups: any unit walking over one collects it for its owner, and uncollected pickups wait there until someone does. A spilled package jumps off the belt and can only be collected once it lands (a fraction of a second); one that breaks in the fall bursts into shards where it lands. A collected one flies into its unit with a +1. Downstream gets nothing until the repair.
- [built] **Gatherer posts** stand beside a belt with a pull point. An idle post grabs a passing package (+1 package for its owner), then works for a while; packages passing meanwhile go on. Upstream posts get first pick, and a post whose rate matches the flow starves everything below it.
- [built] **Post spacing:** no post stands within the post spacing of another post on the same line, measured along the belt, whoever owns it; foundations count. With a post taking half of a belt's flow, two posts drain it, so this is what keeps a side from draining its own belt at home: the map gives each belt exactly one safe slot (Playable map).
- [built] **Control is presence, not capture.** Who gets the flow is decided by whose posts stand furthest upstream and survive, and by breaking segments. Nothing is held by standing in a circle.
- [decided] Segment states include **jammed** (slowed).
- [proposed] Fighting jams only the segment it happens on, not the whole belt.
- [decided] **Packages** are the one spendable currency. **Energy** arrives with buildings. It's a capacity, not a stockpile (C&C-style): power plants supply it, buildings draw it, and a deficit pauses power charge timers, shuts down power-hungry buildings and slows production. Power plants are raid targets off the belt.
  - [decided] Posts draw power once energy exists. Energy is not what limits posts (post spacing is), and it stays out of the MVP until several building types need it: power plants pull fights off the belt, which the MVP is testing.
- [decided] **Stream upgrades multiply what a package is worth, never a post's share of the flow,** and there's no flat income. A multiplier scales with the share a side wins, so it raises the stakes of the fight instead of replacing it; a faster post only drains the belt sooner.
  - [proposed] **Enricher**, the first to try, after navigation and the AI: a building beside a belt turns the packages passing it into high-value ones, visibly different on the belt. Whoever takes them downstream gets the value, the enemy too, so an enricher is an investment the enemy can steal. It gives the "which to raid" choice (see the Rejected second currency).
  - [idea] A global refining upgrade (your posts get more per package), or a source pump (a faster source, which floods the contested middle as well).
  - Risk: a race for the pool can favor fast early posts over fighting; tune the reserve per map. Independent income stays out, or nobody fights over the belt.
- Known risks, to solve with map design and tuning: upstream advantage (answers: one belt per side, post spacing, reversible flow) and snowballing (answers: per-post caps, catch-up rules).

## Units, orders and combat

- [built] **Orders:** move, attack (a unit or post), attack-move, attack a segment, repair a segment (repairers only). Shift queues orders; a plain order replaces the queue. Orders only reach units their issuer owns.
- [decided] Belt-targeted orders still to come: guard segment, raid segment, escort convoy.
- [built] **Fire on the move.** Units fire at the weakest enemy in range whatever they're doing (fewest hit points left, nearest on ties; units before posts), so groups focus fire without clicks. They never stop or chase for it: a move arrives on time, and attack-move is the order that stops to fight. Attack chases into range and fires until the target dies. [built] A unit type that **stops to fire** (artillery) only fires standing still.
- [built] **Return fire.** An idle or attack-moving unit hit by an enemy while nothing is in range to shoot back at chases the attacker into range and fires on it, and idle or attack-moving allies close by join in.
  - **Leash:** it gives up past a leash distance from where the chase began and ignores that attacker until it comes back within the leash. Idle units walk back to their spot; attack-move carries on to its point.
  - **Exceptions:** a move order is never diverted, and no other explicit order is either. Immobile and unarmed units don't answer.
  - After a hit, a unit with nothing in range keeps its turret on the attacker for a few seconds, the one exception to turrets ignoring enemies out of range.
  - Being outranged still loses: the answer is retreat automation (below), not return fire.
  - Units that stop to fire **hold their ground**: they answer only attackers already in range and never chase, so artillery doesn't wander off after whatever shot it.
- [built] **Weapons are data.** Each unit type names a weapon. Damage comes as discrete shots with a reload, not a steady stream.
  - **Delivery:** a bullet hits at once and draws a tracer. A shell is a projectile that homes on its target and hits on arrival. A **ballistic** shell flies an arc to where the target stood when it fired, lands off that point by up to its scatter (less closer in) and bursts there, so a moving target can get away.
  - **Hit:** direct hits only the target. Splash hits every enemy within its radius, less toward the edge, and any open belt segment there.
  - **Minimum range:** a weapon can have one; nothing closer is picked, fired at or answered.
  - **Shots at a segment** land anywhere close around its middle, not on its near edge.
  - **Squads:** a squad's shot is every living member's damage. Under splash, a squad takes damage on the share of its footprint the blast covers, so bunched squads suffer.
  - **No friendly fire** on units, posts or buildings; belts are the one thing splash hurts whoever owns it.
  - **Assignments:** rifle squad bullets; scout car a light machine gun (bullets); tank cannon shells, **direct**; artillery ballistic shells with splash.
  - [decided] Splash is for artillery and later rockets: they're the counter to infantry, and tanks stay anti-vehicle.
  - [decided] Armor classes with a damage-vs-armor table, and ground/air targeting (only anti-air weapons hit air), plug into the same weapon record. One weapon per unit until a unit needs two.
- [built] **Vehicles outrange infantry** (tank > scout car > squad), and artillery outranges everything.
- [built] **Artillery:** slow, fragile, long-ranged, trained at the factory. It stops to fire, has a minimum range and holds its ground (above). Ordered to attack, it closes to a little inside its range, not just to the outer ring, so a target stepping back a meter doesn't move it again. Selected, it shows its range as two rings, minimum and maximum.
  - [decided] The rifle squad lost its unique job (capturing) with the switches. For the MVP it's the cheap unit; it needs a real role before factions.
- [built] **Squads:** fixed size per type, indivisible, one sim entity (one position, one order). Members are slices of one health pool and die one by one, each taking its share of damage with it.
  - Members are formation slots drawn by the view; they become sim state only if that looks wrong in play.
  - [decided] Squads reinforce near base or relay points, for a cost.
- [built] **Vehicles:** a single unit with movement per type (wheeled or tracked). They accelerate, brake and turn at limited rates.
  - Tracked vehicles pivot almost on the spot; wheeled ones need speed to steer, so they arc.
  - **Easing:** everything is eased (ease in, ease out), as physics that reacts to new orders mid-move, not as tween curves.
  - **Reversing:** a stopped vehicle backs up to a close target behind it.
  - **Turrets:** turrets are sim state. They turn at their own rate and fire only once on target. An attack order swings the turret onto its target while the vehicle drives there. Otherwise turrets only turn to enemies already in range, or to whoever just hit them.
  - **Turret arc:** a turret can be limited to so far either side of the nose (artillery). For a target beyond it, a vehicle that isn't driving anywhere pivots its hull round. The view raises a heavy gun's barrel with the distance of the shot.
  - **Wrecks:** a destroyed vehicle blows up and leaves a burnt wreck that smokes for a while and sinks away after 45 s. It's look only, not sim state. [idea] Keep every wreck on the map for salvage.
  - **Lean:** the view leans the hull on its suspension (look only).
- [built] **Navigation:** units go round anything solid: buildings and foundations, posts, turrets, rocks and the map's edge. Belts stand on legs, so they never block.
  - **Size:** paths keep each unit its `radius` (data) from anything solid, so a tank needs a gap of about 4 m and a squad 2 m. Paths run on ground cells half the building grid's (1 m).
  - **Pushing, not steering:** units push each other apart (soft: a crowd overlaps for a moment). One under way or firing holds its ground; an idle one gives way to the side, then walks back to its spot once free. Two meeting head on pass each other.
  - A crowd sent to one point settles into a blob round it instead of shoving for the middle; units sent to spots of their own (a formation) each go to theirs.
  - A building put up on units pushes them out. A move into something solid ends at the nearest spot beside it, on the side the unit comes from.
  - **Fairness:** nothing breaks a tie by map direction, so mirrored sides of a map get mirrored paths (a test plays mirror matches and checks that both sides win some).
  - Not yet: flow fields for big group moves, units steering round each other, terrain costs (roads).
- [built] **Fog of war:** each side sees round its units, buildings and posts, each out to its `sight` (data), a plain radius: nothing blocks sight yet. Sight is kept on 2 m cells over the map.
  - **Hidden:** enemy units, and packages and pickups, show only while seen. Enemy buildings and posts are remembered as last seen (a ghost) until you look again; the ones standing at the start are known from the start.
  - **Belt:** each segment is remembered as last seen, except that a break upstream of one of your posts is news at once, with an alert ("your belt is cut") and a ring on the minimap.
  - **Firing needs sight:** a unit fires only at what its side sees, so long guns need a spotter. An attack order needs a target its side has seen; if the target slips out of sight, the unit goes to where it was last seen and stops.
  - **Giving yourself away:** a unit that hits an enemy is seen by that enemy's side for 3 s, so artillery can be answered.
  - The AI plays under the same fog. `--reveal` (and the demo) shows everything, for watching.
  - Not yet: sight blocked by terrain or rocks, radar, shroud (black, never-seen ground).
- [built] **Start:** each player starts with an HQ, one builder and a little money: no army, no posts. Everything else gets built. (The small test map keeps starting units for its tests.)
- [built] **Production:** buildings train units. The HQ trains builders, the barracks squads and engineers, the factory cars, tanks and artillery.
  - **Cost:** each unit type has a cost and a build time. Cost is paid as it builds, tick by tick, and production stalls while its owner is broke, so production speed follows belt income directly.
  - **Queue:** one per building, first in first out, one unit at a time, with a short cap. Cancelling refunds what was paid.
  - **Repeat:** one toggle per building. Each finished unit goes back to the end of the queue, so a mix keeps its ratio.
  - **Rally point:** finished units leave by the building's exit and spread out around its rally point.
  - **Targets:** buildings and foundations are targets like posts. Losing them can lose the game (Win and lose).
  - **Not yet:** Energy (it comes with several building types) and squad reinforcement.
- [decided] **Veterancy** (after the commander AI, outside the MVP): units rank up from kills, C&C-style, so keeping a unit alive, and repairing it, is worth more than its replacement cost.
- [decided] **Automation:** production repeats, gatherers self-manage, damaged units retreat, abilities are used sensibly. The player can always override. Upgrades are global or per unit type, never per squad. Most units have zero or one active ability.

## Controls

- [built] Right-click does the obvious thing:
  - attack an enemy
  - repair a damaged segment
  - otherwise, move
- [built] Ctrl+right-click a segment attacks it.
- [built] A then left-click attack-moves: left-clicking an enemy attacks it, Shift chains waypoints, and right-click or Esc cancels.
- [built] Double-click a unit to select every unit of its type on screen.
- [built] **Command card** (bottom of the screen), for what's selected; its four slots are Q, W, E, R. It shows names, costs, slot keys, progress, and why placement fails; how-to text (keys, rules) lives in tooltips, as do the HUD's and the packages panel's.
  - A building (selected alone, by clicking it): a button per unit type. A slot key or left-click queues one, T toggles repeat, Backspace cancels the last queued unit and a right-click on a button cancels one of that type. Right-clicking the ground sets the rally point.
  - A foundation: how far along it is, and whether a builder is on it.
  - Builders: a button per building type. A slot key or click arms placement: a ghost follows the cursor, snapped to the grid (shown around it, fading out), green where it fits and red where it doesn't. Left-click places it (the nearest selected builder goes), Shift places more, Z rotates it, right-click or Esc cancels. Right-clicking your foundation with a builder takes over building it.
- [built] **Minimap** (bottom-left): the whole map, with belts and their packages, posts, buildings (foundations hollow), units, and the camera's view. Click or drag to move the camera, right-click to move the selection. Under fog it shows what your side sees, remembered buildings and posts, the belt as last seen, and the fog itself.
- [built] The game opens at 1920×1080; the interface scales with the window.
- [built] **Above the minimap:** your packages, with income per minute over the last 30 s (posts and pickups). The HUD line (top-left) keeps only speed, game time, whose side you're on, and anyone's rebuild clock; every side's numbers, belt counters and performance are under F3.
- [built] **Every word the player reads is translatable:** symbolic keys into gettext .po files, English the fallback; the language is the OS's unless chosen (`--lang=xx` until there's a settings menu). English only for now.
- [built] **Pause menu:** Esc when there's nothing to cancel, or F10. The game stops (the camera still moves) and the world takes no clicks; Resume, Restart, Quit.
- [built] **Game over:** a banner with the winner (or a draw) and the game time, Restart and Quit. The world keeps running behind it.
- [built] Order paths show only for selected units, colored by what the order does: green move, orange attack-move, red attack (a unit or a segment), blue repair.
- [built] The cursor shows what a click will do, computed by the same code that issues the order. Round cursors click at their center; tool cursors point up-left, as on Windows.
- [built] The camera pans with the arrow keys (A is taken by attack-move), screen edges and middle-drag, and zooms with the wheel. It runs on real time, unaffected by game speed.
- [built] Game speed is 1x, 1.5x, 2x or 3x. The sim always runs 20 ticks per sim-second, so speed never changes results.
- [built] F2 hotseat: each side keeps its own camera. Swapping glides to that side's last view, or its spawn on its first visit.
- [decided] The camera is perspective, with pitch 55-60° and field of view 35-40°. Lock it before serious modeling.

## Factions (working titles; final names are Denys's)

- **"Asian" bloc: heavy, slow, sturdy.**
  - Heavily armored, powerful, slow units; structures build slowly but are very sturdy.
  - Builds and upgrades roads. Fastest repairs.
  - Construction: Generals-style dozers. [decided in principle] Foundations must be reinforced before they're built on.
  - Gathering: heavy trucks, slow but big loads (high-value raid targets).
  - Belt: armored segments and fast repairs [decided in principle].
  - [idea] Artillery and area weapons as its counter to infantry.
- **"Western" bloc: fast, technological, expensive.**
  - Mostly aerial plus a basic ground army (like Generals' Air Force General). Anti-air silences it; it depends on money, with paid, limited special powers.
  - Construction: Red Alert-style influence zones from relay points: it builds only where relays extend its area.
  - Gathering: no posts; aerial carriers take packages from the belt to base, with a stealth upgrade.
  - Belt: drones pull packages mid-route and fly over breaks [decided in principle].
  - Rebuild fallback: see open decisions.
- **"Eastern" bloc: partisan, stealthy, opportunistic.** Added last, as a layer on the other two.
  - Taxes captured belt-side civilian buildings instead of using posts; must have base income in every matchup.
  - Construction: occupies civilian buildings, which can't be put down anywhere: only near a supply route, or in the rural areas the map already has.
  - An intermediary between the other two blocs, leaning one way but never acting against itself.
  - Salvages wrecks for reuse or scrap. Captures and converts buildings, or peasants build cheap civilian-style structures, so razing never locks it out.
  - Belt: hidden taps [decided in principle]. (Hijacked junctions went with the junctions.)
  - Its taxes raise civilian annoyance.

## Infrastructure and construction

- [decided] One system: infrastructure objects with states (intact, damaged, broken) that can be repaired or improved. Belt segments are the first user [built]; roads and bridges reuse it.
- [decided] Roads come in two states, worn and improved, which set movement cost. Bridges only over impassable terrain, never over roads, so navigation stays single-layer.
- [built] **Repair** is what builders and engineers do, per unit type (`repairSeconds`, `repairCost`); other units can't. The engineer (barracks, unarmed) repairs but doesn't build, quicker and cheaper than the builder.
  - It costs money and time: a unit repairs at its own rate, paying as the segment heals, and stalls while its owner is broke.
  - **Auto-repair:** an idle repairer fixes damaged open belt within a small radius on its own, while its owner has packages. An order sets priorities.
  - [decided] **What each type repairs is a list:** builders belt and buildings (posts and defenses too), engineers belt, buildings and vehicles. Medics later heal infantry through the same code. A full repair costs a share of the target's own cost, set per repairer type.
- [built] Buildings snap to a 2 m grid, rotated in 90° steps; navigation's cells are half that (1 m), so every building edge is a cell edge.
- [built] **Construction prototype** (generic, before factions):
  - A builder (unarmed, trained at the HQ) walks to the site and lays the foundation on arrival, if the spot is still clear and its owner has the building's whole cost in hand. Placing it is refused up front for the same reasons, and the card says which. The foundation grows only while a builder works on it; more builders don't speed it up, but any of yours can take over an abandoned one.
  - Cost is paid as it grows and it stalls if the money runs out after it started, like production. A foundation starts at a tenth of its health and is a target.
  - Types: barracks, factory, gatherer post (it must stand beside a belt, and becomes a post), turret (it becomes a gun that can't move; tanks outrange it).
  - Placement: anywhere on the map that's clear of buildings, posts, defenses, rocks and belts. Posts also keep the post spacing. A post snaps beside the nearest open belt near the cursor, facing it, and slides along it as the cursor moves; away from a belt it can't be placed.
  - **Post guidance (like RA3's refinery spots):** while a post is being placed, green strips beside the belts mark every free stretch, and a ghost over a spot that's too close to another post jumps along the belt to the nearest free one (within the post spacing).
  - Known risk of "anywhere": a post on the enemy's home stretch, or a turret beside the enemy's home post, in minute one. Acceptable for the prototype.
- [decided] **Factions replace "anywhere" with their own build-area rules** (see Factions): Western relays extend the build area, Asian foundations need reinforcing, Eastern civilian buildings go only near supply routes or in existing rural areas. This removes minute-one exploits and makes players plan their way out of the start.
- [proposed] Belt buildings (posts, relays) snap to sockets beside segments. Posts snap to the belt already [built], continuously rather than to fixed sockets; sockets are for when relays exist.
- [built] **Playable map** (`main.tscn`), generated by `tools/make_main_map.py`: 256×180 m, flat, with rocks. Its layout rules, which any map should follow:
  1. **Point-symmetric** (Blue's half turned 180° is Red's), so it's fair by construction.
  2. **One belt per side,** starting in front of its owner's base. Its **home stretch** is shorter than the post spacing, so exactly one safe post fits: half of your belt, yours as long as you defend it. It runs parallel to the center line (the line equally far from both HQs), so it stays equally safe along its length.
  3. **Then it jogs into the middle,** where the other half is up for grabs: your second post goes there, exposed. An enemy post on your belt has to stand upstream of yours to take anything, so each side is stronger on its own belt, and symmetry evens it out.
  4. **The belts meet in the middle,** close enough (about 17 m) that raiding one puts you next to the other: one front. A band where both run side by side can't also give each a safe home stretch (a band's ends are equally far from both HQs, so a home stretch can't start there), so they meet at one point instead.
  5. **Past the middle, each belt follows the center line out to a flank** and ends at the map edge, equally far from both HQs. Every slot there is contested, and what's left at the end is lost, not anyone's safe income: each side has half a belt guaranteed, and a whole belt's worth is contested. Holding the middle is 3:1.
  6. **Build space** around each HQ stays clear of belts, and both sides are equally far from the middle.
  7. **Rocks** (solid: nothing crosses or builds on them) shape the open ground: a ring of four round the middle, where the belts cross, with lanes between them, and one on each flank beside a belt's tail. They keep 8 m from any belt (posts, spills and repairs are never in them), 40 m from each HQ (room to build), and 10 m from each other (every lane takes a tank).
  8. **The generator checks it:** it prints how far each point along each belt leans toward either HQ, and refuses to write the map unless each belt's safe stretch fits one post only, everything past its second slot is contested, its end is neutral, the belts stay apart, the HQs keep their build space, and the rocks keep their distances.
  - Belts come in covered from beyond the map edge through each owner's back field and open where the home stretch starts; past the flank they're covered again out to beyond the edge. The generator's rules count open belt only, and it checks that each belt starts and ends beyond the map.
  - No prebuilt posts: players build them.
- [proposed] **Income vs spending:** full production of the dearest unit costs more than a side's income from its home post alone, so extra belt income always buys something. Costs live in `data/units.json`.

## Win and lose (skirmish)

[built] A player loses when they can no longer rebuild: **no buildings, and either no builders or not enough money for the cheapest building.** A 60 s grace timer runs while there are no buildings but recovery is still possible. It's one function, `CanStillRecover(player)`, run every tick, backed by a unit-test table of edge cases. What's built, generic before factions:

- **Buildings** are finished production buildings (HQ, barracks, factory) and posts. Turrets don't count: a lone turret isn't a base. Foundations don't count either: they only pause the clock.
- **Money for the cheapest building:** the cheapest non-turret building a living builder can put up, or what's still owed on one of your own foundations, whichever is less. Paying into a foundation never makes you lose.
- **The clock** pauses only on ticks a builder is working a foundation (an abandoned one doesn't), resumes where it was when work stops, and clears once a building is finished.
- **Broke means out:** no buildings and not enough money loses at once, with no clock, even if income may still arrive.
- **Losing destroys everything the player has left** (units, turrets, posts, foundations), then the game-over banner shows. The game ends when at most one player is left; if the last ones go on the same tick, it's a draw.
- End conditions are on in play and off in the stress scene, which has no buildings.

Still [decided], for when their systems exist:

- captured civilian buildings count as buildings (Eastern)
- builders inside transports count
- cargo in transit does not count as money; refunds do
- free options count, such as a peasant capturing a free building
- income that needs no buildings counts
- the timer pauses while a foundation is being built, and resumes (doesn't reset) when it's destroyed or left [built]
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

1. **Grid and MVP map:** [built] a 2 m building grid and 1 m navigation cells; units are sized by a radius in their data, not by cell footprints (a tank fits a 4 m gap, not only 6 m). The playable map is 256×180 m; grow it if fights feel cramped.
2. **Belt flow:** [built] fixed-rate sources, finite on the main map; packages reaching an end go back into their source's reserve (see Belt economy).
3. **Western rebuild fallback:** a mobile relay unit, or an airdropped first structure.
4. **When every source is dry:** the fight goes on with what's banked, and destruction decides. Revisit if matches stall. They can: the scripted AI sometimes ends up in mirror matches with both sides broke, every post gone and too little left in the sources to matter, and neither attacks. Look at it again with the behaviour-tree AI and real playtests.
5. **Fog of war:** [built] grid-based over the navigation bounds (2 m cells), radius sight only (see Units: Fog of war).
6. **Baseline test machine:** undecided (Steam Deck or a mid-range laptop).
7. **Name:** working title *Triple Point*. [decided] The code is open source under Apache 2.0 (public on GitHub); art, audio, story and the name are not covered by it and are what a release sells. Code names stay neutral (`Game.sln`, assembly `Game`).
8. **Tax and unrest balance, final faction names:** later, Denys's call.

## Rejected (don't re-propose without a new reason)

- **Two belt currencies:** they clutter the belt, the one thing that must read instantly, and double the cost tuning across three factions. High-value packages give the "which to raid" choice more cheaply.
- **Capture points** (switches taken by holding a circle for a few seconds): timer-based control from Relic games; it feels artificial. Control comes from posts that survive and segments that break.
- **Junctions** (merges and switches): they complicated the belt without adding a decision posts and breaks don't already give. Every belt is one line from a source to an end; so no loops either.
- **Splash damage on tank shells:** tanks would be best at everything; splash belongs to artillery and rockets.
- **Turrets tracking enemies out of range, with no order:** too twitchy. Only an attack order aims ahead, and a hit makes the turret watch its attacker for a while.
- **Upfront payment for production (Generals style):** repeat would wait until the full cost was banked, and it hides how belt income becomes units.
- **Units that stop firing while moving:** units fire on the move; only a future per-type flag for heavy or emplaced weapons.
- **Tween curves for vehicle motion:** easing is physics, so it reacts to new orders mid-move.
- **Navmesh, TOML game data, TAA, ECS (for now):** reasons in `docs/architecture.md`.
