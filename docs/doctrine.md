# Design doctrine

What the game is and the rules it follows. Every rule carries a status:

- **[built]** in the code now; the code is the reference for details.
- **[decided]** agreed, not built yet.
- **[proposed]** a default to confirm in playtest.
- **[idea]** worth keeping, not agreed.

Rules, not numbers: tuning values live in `data/*.json` and the constants at the top of `src/Sim/Simulation.cs`. A number appears here only when it states a design relation (X outranges Y). When a decision changes in conversation, change it here in the same session, and flip tags as things get built.

## Pitch and pillars

A Command & Conquer Generals-style RTS, set around 2050-2100. Three asymmetric blocs fight for control of the economy.

- **A shared, physical supply economy.** Packages ride neutral supply trucks along roads across the map, and they're the one currency. Every package you take is one your enemy doesn't get.
- **Automate the clicks, not the choices.** The player decides where and when; the game handles how. Every button is a decision, not upkeep.
- **Thinking over rushing.** Fights cost economy, map control matters, infrastructure breaks and gets repaired.
- **Campaign first**, grounded and morally complex. The story is Denys's alone.

References: C&C Generals / Zero Hour (factions, feel), Company of Heroes and Iron Harvest (readable units, grounded worlds), Frostpunk (desaturated mood), Titanfall 2 (teaching through level design), Tooth and Tail (accessible control).
Anti-references: Tempest Rising (muddy visuals, vague controls), Dawn of War squad-upgrade itemization, HoI4 micromanagement.

## MVP

**The one question: is fighting over the supply routes fun?** (Over the belt, until the belt became trucks on roads: see Supply routes.) Anything that doesn't help answer it waits.

Milestones:

1. [built] Camera, selection, move orders.
2. [built] Supply routes, trucks, depots, a debug overlay (HUD counters, under F3). (Built first as a belt, packages and gatherer posts.)
3. [built] Break and repair road pieces; shoot trucks, spill and collect their cargo. (Merges and switches were built, then removed: see Rejected.) Not yet: jammed pieces.
3.5. [decided] Symmetric route test: a mirror match with generic units, before asymmetry. This way a failed asymmetric test can be traced to the factions, not the routes.
4. Two factions (Asian vs Western), 2-3 units each, one of them an infantry squad. Groundwork [built]: two players, ownership, combat with return fire, a rifle squad, a rocket squad (beyond the 2-3, chosen 2026-10-01: the counter loop needs an answer to tanks on foot), a scout car and a tank, production, a construction prototype (builder, barracks, factory, depot, turret), a minimap, F2 hotseat, win/lose, navigation and fog of war (see Units: Navigation, Fog of war), built after the AI so there was an opponent to test them against.
5. [built] **A basic commander AI that fights over the routes** (v1, scripted), playing Red on the main map:
   - Economy: two builders; depots on its own half, nearest home first, never where enemy fighters stand; a barracks, then a factory; then depots saved for up to its level's `SavedPosts` (normal 4, easy 2), and more while money allows and a spot is free (since depots further down the route take more, a normal AI that spent everything on units lost to easy: it won 7 of 16; saving for four depots won 13 of 16). It saves for a building it wants instead of training, and keeps the money while a builder walks to a site (a building starts only with the whole cost in hand).
   - [built] Defenses: once the barracks and factory stand, it saves for its level's number of defenses (`AiSettings.Defenses`, 1 for both levels), each beside a depot none guards yet, nearest home first, toward home; turrets and heavy turrets in turn. Measured between AIs: a defense at every depot stalled matches past 30 minutes, and normal saving for a second (a heavy turret) lost to easy in every game; the money does more as units.
   - [built] Garrisons: the nearest idle squad goes into each free garrison building on its half, and stays to hold it (it's no longer part of the army).
   - [built] **Variety:** it draws from the sim's one RNG: its first attack comes up to 30 s either side of its level's time, and three times in ten a building trains any of its fighters, not the one it has fewest of. So the same seed gives the same match, and different seeds different ones (without it the seed barely mattered: sixteen mirror matches were two). The RNG scrambles its seed, since xorshift's first draws from small seeds are tiny and always leaned the same way.
   - Army: one engineer once it has a depot, then whichever fighter it has fewest of (artillery counts double). Rally at a staging point in front of home.
   - Fighting: it defends anything of its own under attack, once the enemy has been in view there a moment; from a set game time on, with 10 fighters and 1.5× the enemy's strength it pushes on the nearest enemy building; with 6 it raids the enemy's most exposed depot, and sends its artillery (only splash breaks road) to break the enemy's road upstream of their depots (within 30 m, the weakest piece: dirt before paved), but never road that feeds its own. Units below 30% health pull back to mend (infantry at its barracks, vehicles at its factory) until whole, unless fewer healthy fighters are left than a raid needs: then everyone fights.
   - Upkeep: repairers mend broken road feeding its depots when no enemy is near; idle fighters shoot passing trucks that won't unload at any of its depots, while no enemy fighter is close; idle units pick up spilled packages that no enemy stands near.
   - Scouting: when part of the enemy's road hasn't been seen for 45 s, its fastest fighter (never artillery) goes to look.
   - It knows only what its side sees and remembers, through one knowledge layer (`AiView`): the same fog as a player.
   - [built] **Levels:** easy and normal (`--ai-level=`, or SimHost's `AiLevel`; normal by default). A level only makes it worse at things that are plainly bad, never changes its plan. The numbers are `AiSettings`: how often it thinks, idle time between units, the fighters it gathers before a raid and a push, the game time before it attacks at all, how long an enemy near its things must be in view before the army answers, whether hurt units pull back, and how many defenses it saves for. Easy is slower on every one and doesn't pull back. Handicaps to its economy (one builder, fewer posts) made it stronger, so they're out.
   - [built] **Nerfed for a learning player (2026-09-30):** Denys's matches said it attacked too early and won every fight. Normal now thinks once a second, raids with 6 fighters (was 4) and pushes with 10 (was 8), not before 5:00 (it raided at 3:32), and answers a threat after 3 s in view instead of at once. Between AIs the first raid moved from 3:32 to about 5:35; mirror matches still go either way. A test checks normal beats easy. A hard level that only thought faster didn't beat normal once extra depots paid; a real one waits for the behaviour tree.
   - [decided] **The AI after v1 is a behaviour tree** that decides for itself. v1 is scripted to prove the route fight first. Its steps (survey, build, produce, fight, mend, collect, scout) are the future tree's subtrees, and `AiView` is its blackboard.

6. [built] A UI layout pass: corner panels, the positional command grid, the selection panel, Stop, Hold, control groups, the idle-builder button (see Controls). [built] Neutral roadside buildings that infantry garrison (go in, come out), for ambushes on the routes (7.2 below). Civilian unrest stays out of the MVP.

7. [decided] **The batches before the friends playtest (planned 2026-09-30, after the third match):**
   1. [built] Mending at home, building repair and retreat (Units): until then nothing healed, so every fight was pure attrition.
   2. [built] Garrison buildings (below), and the AI taught to use them and to guard a depot with a turret (item 5: Defenses, Garrisons).
   3. [built] Road types on the main map (Infrastructure: Road types): a dirt track and a paved road, so routes carry different stakes.
   4. [built] A readability pass (Look): sides at a glance and unit class silhouettes, judged by the grayscale and squint test (2026-10-01). Left for later: a calmer environment (the rocks are the brightest thing on screen), and threat markers.
   5. [built] A skirmish setup menu (Controls: Main menu, 2026-10-01), then the playtest.
   - [built] **Garrisons:** neutral buildings on the routes, on the main map (two houses at the crossing: Playable map). How many infantry squads (any on foot) a building holds is its type's `garrison` in buildings.json, so big and small buildings come from data (2 to start). They enter one by right-clicking it and leave with Exit on the card (Q, everyone), a click on a squad's tile in the selection panel (that one), or any order given them; whoever is inside owns it until the last one leaves. Inside they fire out at +2 m range (from the building's wall), take no damage and can't be seen: the building soaks every hit (armor applies: a structure, so artillery at ×3 is the answer), and when it falls they tumble out at half health.
     - An empty one is nobody's: nothing targets it or splashes it, and a side that loses leaves its houses empty. A house never keeps a side in the game (Win and lose).
     - It's part of the map, so it always shows: walls in its holder's color (gray while nobody holds it; under fog, as last seen) and a lit pip on the roof per squad inside.

After that, play against the AI with friends. If they ask for more, build a vertical slice: the first 2-3 missions of one faction.

### Playtests

- [built] **Match reports:** every skirmish (not the demo) that ends, or runs a minute before it's left, writes a report to `user://matches`: totals per side (packages gathered, picked up and spent, posts and buildings built and lost, units trained and lost with their cost), belt breaks by whose shot and whose posts they cut off, the first time each building and unit type appeared, and every side's money, income, posts and army every 30 s. The event log names each thing's owner and type.
- **2026-09-30, Denys vs the AI (normal), main map:** lost at 4:54: out-built (one post for two minutes against the AI's four), raided, then overrun. It felt:
  - **Slow to start.** The belts start empty, and no package reaches a post for about 75 s.
  - **The AI was too strong** for a first match, hence the levels.
  - **The belt didn't matter.** One segment broke all match; it was a fight over posts and the base.
- **2026-09-30, Denys vs the AI (normal, after the first nerf), main map:** won at 7:55. Red went broke from 2:30 to 5:00, trained four engineers, and lived on with two depots and no HQ or builder. It felt:
  - **Artillery too weak on buildings:** rifle squads out-killed it. Hence structure damage (Weapons).
  - **A side with only depots isn't a side:** hence depots no longer count (Win and lose).
  - **Attack-move didn't engage** what infantry could see but not reach; hence attack-move goes for it (Units).
  - **Roads broke to machine guns, and looked like tiles:** only splash breaks road now, and roads have asphalt, edge lines, a dashed center line and verges, with no seams between pieces.
  - Still to look at: why the AI's economy stalls (broke for minutes, engineers replaced as they die).
- **2026-09-30, third match, Denys vs the AI (normal):** stopped at 8:33, the AI ahead: it took the depot fight right after its first raid at 5:00, and its economy held (4 depots from 2:00, everything spent). Found: tanks shot infantry instead of tanks (hence target priority, Units), and a new attack order drew a waypoint through the old target (the path to it lingered; a new order now drops it).
- **2026-10-01, fourth match, Denys vs the AI (normal):** stopped at 6:00, even-ish (the report: about 40 packages a minute each, armies of 5-8). Found, and changed the same day:
  - **The houses were useless** beside the tails, where nobody fought: they moved to the crossing (Playable map).
  - **No way to bring one squad out:** a tile per squad inside now does (Garrisons).
  - **Turtlish:** no reason to leave home, and a slow start. A depot's share now grows down the route (Supply routes), and each side starts with 50 packages instead of 20 (a depot and a barracks at once).
  - **No armor:** machine-gun infantry shot down buildings and tanks. Weapons now have a factor per target class, and the rocket squad answers tanks on foot (Units).
- **What AI-vs-AI numbers add** (match reports and `AiTests`):
  - Income is flat at about 1 package/s per side from 1:30 on, however many posts: a post downstream of another only gets what that one lets past, so posts beyond the first on a belt buy a spare, not income.
  - Belt breaks are rare between AIs too, about one a match.
  - Between AIs, patience wins: the side that attacks first, into the other's base and its fresh production, loses.
- **What changed (2026-09-30):** the belt became neutral supply trucks on roads (Supply routes; the belt is on the Rejected list). A depot takes a third of each truck, so a third flows on past a side's own two depots into the contested middle; a break holds trucks where they can be shot; a shot truck spills its load for whoever is there; routes start full. Between AIs since: 2-7 road breaks a match instead of about one, and in some matches trucks shot and their loads picked up; the scripted AI still rarely shoots trucks. Next: a playtest on it (open decision 9).

**Not in the MVP:** the Eastern bloc, bridges, civilian unrest, multiplayer and lockstep, normal maps, FSR, CMAA2, ECS, save/load, wear transitions, decals, damage stages (a color swap per state is enough).

## Supply routes

The economy runs on neutral supply trucks driving fixed roads across the map. They replaced conveyor belts (see Rejected). In the code a route is still a belt line, a truck a package and a depot a gatherer post, until the trucks prove themselves.

- [built] **Routes** are fixed lines, authored per map, each from a source beyond the map to an end beyond it, with no junctions (see Rejected). Players never build road. Routes are cubic Bezier curves, cut at load into short breakable pieces. Trucks drive by arc length at the route's speed, keep a minimum spacing, and leave the source with a full load. A queue that reaches the source holds the next truck back, and it never leaves.
- [built] **Nobody owns the trucks:** whoever takes their cargo gets it.
- [built] **Routes start full:** at the start trucks are already all along each route, as if it had been running, so the first depot earns within seconds.
- [built] **Sources are finite, and a route returns what nobody takes.** Each source holds a reserve (set per map). What a truck still carries at the end goes back into the reserve beyond the map, so supply leaves the pool only through depots and destroyed trucks: a destroyed truck is the one waste, and raiding burns supply both sides share. A gauge on the covered stretch where the route comes into play shows what's left.
- [built] **Depots** stand beside a road with a pull point. Every truck with cargo that reaches one stops a moment and unloads the depot's share for its owner, then drives on with the rest; upstream depots unload first. (The belt's posts took every other package, so a side's own two drained its belt at home and the contested middle carried nothing.)
  - [built] **The share grows down the route (2026-10-01):** a sixth of a full load where the open road starts, rising evenly to half at its end (12-package trucks: 2 at home, 4 midway, 6 at the tail). The safe home depot keeps a side going; the money is out in the contested stretch (playtest: with a third everywhere there was no reason to leave home). Placing a depot shows what that spot takes. Numbers: `FirstDepotShare`, `LastDepotShare` in `Simulation.cs`.
- [built] **Depot spacing:** no depot stands within the spacing of another on the same route, whoever owns it; foundations count. It keeps a side from emptying its own route at home: the map gives each route one safe slot (Playable map), a second on its own half, and the rest flows on into the contested middle, where depots take the most.
- [built] **Road pieces** have health, and **only splash damages them** (2026-09-30 playtest: breaking a road with a machine gun made no sense). So only splash weapons (artillery) take the order to shell a piece (Ctrl+right-click with one selected); nothing picks a road as its target on its own. Splash hurts any open piece in its radius, whoever's route it is, so artillery fighting beside a road breaks it by accident. A damaged piece still works. A broken one stops traffic until it's repaired back to full health (Repair): trucks on it stay put, and the ones coming wait before it and queue back toward the source. A break wastes nothing, since the trucks wait; it holds the flow, and holds the trucks where they can be shot.
- [built] **Trucks can be shot**, but only on purpose: Ctrl+right-click one (nobody's units pick a truck on their own), or a blast that catches it.
  - A destroyed truck spills its cargo on spots beside the road round it. A share breaks in the fall (spill loss, per route); the rest are pickups that any unit collects for its owner by walking over them, and they wait there however long.
  - A spilled package jumps off and can only be collected once it lands; one that breaks bursts into shards; a collected one flies into its unit with a +1. The wreck is look only, and sinks away.
  - So a held break taps the route: the trucks queue, and whoever holds it takes their loads.
- [built] **Trucks stop for units in the way.** An idle unit on the road steps off it, to the right of the way the truck drives, and stays off; one that's moving or fighting there holds the truck up until it moves on.
- [built] **Covered stretches:** where a route runs beyond the playable area, and through its owner's back field to where the open road starts, it's covered. Its pieces can't be shot or broken (nobody could reach them to repair them), trucks in it can't be seen or shot, no depot goes beside it (so the back field adds no safe slots), and units there don't hold trucks up. Maps set each route's covered start and end.
- [built] **A route reads as breakable pieces:**
  - The road is a flat strip on the ground (asphalt with painted lines, or a dirt track with ruts: Road types), one road without seams. A damaged piece tints and shows a health bar; a broken one is torn slabs round a crater, flattening back as it's repaired. Covered stretches are a closed housing. Roads never block movement.
  - A truck is a cab and a bed with crates on it, as many as its load left, with a health bar once hurt.
  - With units selected, the piece or truck under the cursor shows what a right-click would do, and a hint beside the cursor gives its health (and a truck's load) and the keys.
- [built] **Control is presence, not capture.** Who gets the flow is decided by whose depots stand furthest upstream and survive, by breaking road and by shooting trucks. Nothing is held by standing in a circle.
- [decided] Road pieces also get a **jammed** state (slowed).
- [proposed] Fighting jams only the piece it happens on, not the whole route.
- [decided] **Packages** are the one spendable currency. **Energy** arrives with buildings. It's a capacity, not a stockpile (C&C-style): power plants supply it, buildings draw it, and a deficit pauses power charge timers, shuts down power-hungry buildings and slows production. Power plants are raid targets off the routes.
  - [decided] Depots draw power once energy exists. Energy is not what limits depots (depot spacing is), and it stays out of the MVP until several building types need it: power plants pull fights off the routes, which the MVP is testing.
- [decided] **Stream upgrades multiply what a package is worth, never a depot's share of a truck,** and there's no flat income. A multiplier scales with the share a side wins, so it raises the stakes of the fight instead of replacing it.
  - [proposed] **Enricher**, the first to try: a building beside a road turns the cargo of trucks passing it into high-value packages, visibly different on the truck. Whoever takes them downstream gets the value, the enemy too, so an enricher is an investment the enemy can steal. It gives the "which to raid" choice (see the Rejected second currency).
  - [idea] A global refining upgrade (your depots get more per package), or a source pump (more trucks, which floods the contested middle as well).
  - Risk: a race for the pool can favor fast early depots over fighting; tune the reserve per map. Independent income stays out, or nobody fights over the routes.
- Known risks, to solve with map design and tuning: upstream advantage (answers: one route per side, depot spacing, reversible flow) and snowballing (answers: per-depot caps, catch-up rules).

## Units, orders and combat

- [built] **Orders:** move, attack (a unit, depot, building or truck), attack-move, attack a segment (splash weapons only), repair a segment (repairers only). Shift queues orders; a plain order replaces the queue. Orders only reach units their issuer owns.
- [decided] Route orders still to come: guard a road piece, raid a stretch, escort a convoy. [built] Stop and hold position (Controls).
- [built] **Fire on the move.** Units fire at the weakest enemy in range whatever they're doing (fewest hit points left, nearest on ties; units before depots and buildings; never trucks), so groups focus fire without clicks. [built] **Target priority:** among units, a weapon first picks the class it `prefers` (weapons.json): tank cannon and heavy turret go for vehicles (defenses count as vehicles), rifles, machine guns and artillery for infantry; the weakest within it, then the rest (playtest: tanks spent their shells on infantry, where a hit fells one member). They never stop or chase for it: a move arrives on time, and attack-move is the order that stops to fight. [built] **Attack-move also goes for what it sees:** the nearest enemy its side sees within its range + 8 m (never beyond its own sight; units before depots and buildings), which it closes in on and fights, then carries on to its point (playtest: infantry walked past enemies it could see but not yet reach). Attack chases into range and fires until the target dies. [built] A unit type that **stops to fire** (artillery) only fires standing still.
- [built] **Return fire.** An idle or attack-moving unit hit by an enemy while nothing is in range to shoot back at chases the attacker into range and fires on it, and idle or attack-moving allies close by join in.
  - **Leash:** it gives up past a leash distance from where the chase began and ignores that attacker until it comes back within the leash. Idle units walk back to their spot; attack-move carries on to its point.
  - **Exceptions:** a move order is never diverted, and no other explicit order is either. Immobile and unarmed units don't answer.
  - After a hit, a unit with nothing in range keeps its turret on the attacker for a few seconds, the one exception to turrets ignoring enemies out of range.
  - Being outranged still loses: the answer is retreat automation (below), not return fire.
  - Units that stop to fire **hold their ground**: they answer only attackers already in range and never chase, so artillery doesn't wander off after whatever shot it.
- [built] **Weapons are data.** Each unit type names a weapon. Damage comes as discrete shots with a reload, not a steady stream.
  - **Delivery:** a bullet hits at once and draws a tracer. A shell is a projectile that homes on its target and hits on arrival. A **ballistic** shell flies an arc to where the target stood when it fired, lands off that point by up to its scatter (less closer in) and bursts there, so a moving target can get away.
  - **Hit:** direct hits only the target, never road. Splash hits every enemy within its radius, less toward the edge, and any open road piece and truck there.
  - [built] **Armor (2026-10-01, Generals-style, from the gun's side):** each weapon has a factor on its damage per target class (`against`): infantry (on foot), vehicles (everything else, defenses too) and structures (buildings, foundations, depots). Small arms barely scratch vehicles (×0.15-0.2) and buildings (×0.3); artillery is ×3 on structures and ×0.5 on vehicles; the tank cannon ×1.5 on structures; rockets ×0.5 on infantry. Trucks and road take damage as it is, so infantry can still raid trucks. (Playtests: rifle squads out-killed artillery on buildings, and machine-gun infantry shot down tanks.)
  - [built] **The counter loop:** rifles beat rockets, rockets beat tanks (and the heavy turret), tanks beat rifles (and the machine-gun turret), each for the same money; `TurretTests` holds it with the real data.
  - **Minimum range:** a weapon can have one; nothing closer is picked, fired at or answered.
  - **Shots at a road piece** land anywhere close around its middle, not on its near edge.
  - **Squads:** a squad's shot is every living member's damage. Under splash, a squad takes damage on the share of its footprint the blast covers, so bunched squads suffer. [built] A direct hit fells one member at most, so big single shots (tank cannon, heavy turret) waste most of their damage on infantry, and a rocket volley at a squad (×0.5) doesn't fell even one.
  - **No friendly fire** on units, depots or buildings; roads and trucks are the things splash hurts whoever's they are.
  - **Assignments:** rifle squad bullets; rocket squad rockets (direct shells, anti-vehicle); scout car a light machine gun (bullets); tank cannon shells, **direct**; artillery ballistic shells with splash.
  - [decided] Splash is for artillery: it's the counter to infantry, and tanks and rockets stay anti-vehicle.
  - [decided] Armor classes with a damage-vs-armor table, and ground/air targeting (only anti-air weapons hit air), plug into the same weapon record. One weapon per unit until a unit needs two.
- [built] **Vehicles outrange infantry** (tank > scout car > squad), and artillery outranges everything.
- [built] **Artillery:** slow, fragile, long-ranged, trained at the factory. It stops to fire, has a minimum range and holds its ground (above). Ordered to attack, it closes to a little inside its range, not just to the outer ring, so a target stepping back a meter doesn't move it again. Selected, it shows its range as two rings, minimum and maximum.
  - [decided] The rifle squad lost its unique job (capturing) with the switches. For the MVP it's the cheap unit; it needs a real role before factions.
- [built] **Squads:** fixed size per type, indivisible, one sim entity (one position, one order). Members are slices of one health pool and die one by one, each taking its share of damage with it.
  - Members are formation slots drawn by the view; they become sim state only if that looks wrong in play.
  - [built] **Mending at home (2026-09-30, Generals-style):** nothing heals on its own. Right-click your barracks with infantry, or your factory with vehicles: they go there and mend, a squad regaining its lost members as its health comes back, paid as it heals (a share of the unit's cost for a full mend) and stalling while broke. Which class a building mends is data (`mends` in buildings.json). Relay points may mend too when a faction has them.
- [built] **Vehicles:** a single unit with movement per type (wheeled or tracked). They accelerate, brake and turn at limited rates.
  - Tracked vehicles pivot almost on the spot; wheeled ones need speed to steer, so they arc.
  - **Easing:** everything is eased (ease in, ease out), as physics that reacts to new orders mid-move, not as tween curves.
  - **Reversing:** a stopped vehicle backs up to a close target behind it.
  - **Turrets:** turrets are sim state. They turn at their own rate and fire only once on target. An attack order swings the turret onto its target while the vehicle drives there. Otherwise turrets only turn to enemies already in range, or to whoever just hit them.
  - **Turret arc:** a turret can be limited to so far either side of the nose (artillery). For a target beyond it, a vehicle that isn't driving anywhere pivots its hull round. The view raises a heavy gun's barrel with the distance of the shot.
  - **Wrecks:** a destroyed vehicle blows up and leaves a burnt wreck that smokes for a while and sinks away after 45 s. It's look only, not sim state. [idea] Keep every wreck on the map for salvage.
  - **Lean:** the view leans the hull on its suspension (look only).
- [built] **Navigation:** units go round anything solid: buildings and foundations, depots, turrets, rocks and the map's edge. Roads never block; trucks don't either (see Supply routes for who waits for whom).
  - **Size:** paths keep each unit its `radius` (data) from anything solid, so a tank needs a gap of about 4 m and a squad 2 m. Paths run on ground cells half the building grid's (1 m).
  - **Pushing, not steering:** units push each other apart (soft: a crowd overlaps for a moment). One under way or firing holds its ground; an idle one gives way to the side, then walks back to its spot once free. Two meeting head on pass each other.
  - A crowd sent to one point settles into a blob round it instead of shoving for the middle; units sent to spots of their own (a formation) each go to theirs.
  - A building put up on units pushes them out. A move into something solid ends at the nearest spot beside it, on the side the unit comes from.
  - **Fairness:** nothing breaks a tie by map direction, so mirrored sides of a map get mirrored paths (a test plays mirror matches and checks that both sides win some).
  - Not yet: flow fields for big group moves, units steering round each other, terrain costs (roads).
- [built] **Fog of war:** each side sees round its units, buildings and depots, each out to its `sight` (data), a plain radius: nothing blocks sight yet. Sight is kept on 2 m cells over the map.
  - **Hidden:** enemy units, trucks and pickups show only while seen. Enemy buildings and depots are remembered as last seen (a ghost) until you look again; the ones standing at the start are known from the start.
  - **Roads:** each piece is remembered as last seen, except that a break upstream of one of your depots is news at once, with an alert ("your supply road is cut") and a ring on the minimap.
  - **Firing needs sight:** a unit fires only at what its side sees, so long guns need a spotter. An attack order needs a target its side has seen; if the target slips out of sight, the unit goes to where it was last seen and stops.
  - **Giving yourself away:** a unit that hits an enemy is seen by that enemy's side for 3 s, so artillery can be answered.
  - The AI plays under the same fog. `--reveal` (and the demo) shows everything, for watching.
  - Not yet: sight blocked by terrain or rocks, radar, shroud (black, never-seen ground).
- [built] **Start:** each player starts with an HQ, one builder and 50 packages (a depot and a barracks at once; 20 made the first two minutes waiting): no army, no depots. Everything else gets built. (The small test map keeps starting units for its tests.)
- [built] **Production:** buildings train units. The HQ trains builders, the barracks squads and engineers, the factory cars, tanks and artillery.
  - **Cost:** each unit type has a cost and a build time. Cost is paid as it builds, tick by tick, and production stalls while its owner is broke, so production speed follows income directly.
  - **Queue:** one per building, first in first out, one unit at a time, with a short cap. Cancelling refunds what was paid.
  - [built] **Queue:** no real limit (a sane 999), and Shift queues or cancels five at a time (2026-09-30), so a long queue does what a repeat toggle did.
  - **Rally point:** finished units leave by the building's exit and spread out around its rally point.
  - **Targets:** buildings and foundations are targets like depots. Losing them can lose the game (Win and lose).
  - **Not yet:** Energy (it comes with several building types) and squad reinforcement.
- [decided] **Veterancy** (after the commander AI, outside the MVP): units rank up from kills, C&C-style, so keeping a unit alive, and repairing it, is worth more than its replacement cost.
- [built] **Retreat automation (2026-09-30):** a per-unit toggle on the command card, off by default, so nothing moves without being asked. With it on, a unit below 30% health goes to mend at your nearest building that mends its class (barracks for infantry, factory for vehicles). It stays there until mended, then is idle.
- [decided] **Automation:** long production queues, gatherers self-manage, damaged units retreat (on the toggle), abilities are used sensibly. The player can always override. Upgrades are global or per unit type, never per squad. Most units have zero or one active ability.

## Controls

- [built] Right-click does the obvious thing:
  - attack an enemy
  - repair a damaged road piece
  - otherwise, move
- [built] Ctrl+right-click a truck attacks it; a road piece, only with artillery (splash) selected, and the hint over a road says so otherwise.
- [built] A then left-click attack-moves: left-clicking an enemy attacks it, Shift chains waypoints, and right-click or Esc cancels.
- [built] Double-click a unit to select every unit of its type on screen.
- [built] **Layout: corner panels.** A strip along the top (the HUD line on the left, packages in the middle, alerts on the right), the minimap bottom-left, the selection panel bottom-center, the command card bottom-right, and the idle-builder button just above the minimap.
- [built] **Nothing in the interface changes size with what it shows.** Every panel, cell and tile has a fixed size, and changing numbers get room reserved for their widest sane value: packages up to 9999, income up to +999/min, a count per tile up to 999, idle builders up to 99, a queue up to 99. Text that runs longer is clipped, never wrapped, and the full text is in the tooltip.
- [built] **Command card** (bottom-right): a 4×3 grid, fixed by position like Generals, on physical keys so it's the same on any layout. Q W E R / A S D F / Z X C V; each cell's key is in its corner. It shows names, costs, and why placement fails; how-to text (keys, rules) lives in tooltips, as do the HUD's and the packages panel's.
  - A building (selected alone, by clicking it): the top row is its unit types. A cell's key or left-click queues one, and with Shift five; the queued count (up to 999) is a badge in the cell's corner and the one in production a bar along its bottom (red while stalled for money). A right-click on a cell cancels one of that type and Backspace the last queued unit, each five with Shift. Right-clicking the ground sets the rally point.
  - A foundation: how far along it is, and whether a builder is on it.
  - Units: A attack-move, S stop, D hold position, X auto-retreat (on or off), whatever is selected. Builders also get their building types in the top row. A cell's key or click arms placement: a ghost follows the cursor, snapped to the grid (shown around it, fading out), green where it fits and red where it doesn't. Left-click places it (the nearest selected builder goes), Shift places more, Z rotates it, right-click or Esc cancels. Right-clicking your foundation with a builder takes over building it.
- [built] **Command card, builders:** five building types, Q W E R then F.
- [built] **Minimap** (bottom-left): the whole map, with roads and their trucks, depots, buildings (foundations hollow), units, and the camera's view. Click or drag to move the camera, right-click to move the selection. Under fog it shows what your side sees, remembered buildings and depots, roads as last seen, and the fog itself.
- [built] The game opens at 1920×1080; the interface scales with the window.
- [built] **Selection panel** (bottom-center): one unit shows its health and what it's doing (moving, attacking, holding, answering fire...); a building shows its health and what it's training; several units show a tile per type with a count and the group's health. Clicking a tile keeps only that type; Shift+click drops it.
- [built] **Stop (S)** drops every order; the units still shoot what comes in range and answer fire. **Hold (D)** also drops orders, then holds the spot: shoot what's in range, never chase, never give way when pushed; on a road, trucks can't pass. Any new order ends the hold.
- [built] **Control groups:** Ctrl+1..9 makes the selection a group, 1..9 selects it, a double tap takes the camera there. Groups are per side.
- [built] **Idle builders:** a button above the minimap shows how many of your builders have nothing to do; it or `.` selects the next one and looks at it.
- [built] **Top strip, center:** your packages, with income per minute over the last 30 s (depots and pickups). The HUD line (top-left) keeps only speed, game time, whose side you're on, and anyone's rebuild clock; every side's numbers, route counters and performance are under F3.
- [built] **Every word the player reads is translatable:** symbolic keys into gettext .po files, English the fallback; the language is the OS's unless chosen (`--lang=xx` until there's a settings menu). English only for now.
- [built] **Main menu (2026-10-01):** the game opens on it: Skirmish, Controls, Settings, Quit.
  - **Skirmish:** your side (Blue or Red) and the opponent: Easy AI, Normal AI, or Hotseat (two players at one computer, F2 hands over). Start plays the main map with that setup, and Restart keeps it. The menu remembers the last choice while the game runs.
  - **Controls:** every key, read from the Input Map, and the game's big ideas in six lines (trucks and depot share, cutting roads and shooting trucks, repair and paving, the counter loop, houses, how a side is out).
  - **Settings:** fullscreen or windowed, and the interface size: Compact or Normal (larger doesn't fit the 1152×648 canvas the panels are laid out on). Saved in `user://settings.cfg` and applied when the menu opens, so tools that open a scene directly keep their own window.
  - Not in it, by choice: starting money, a fog toggle, the match report (still written to `user://matches`). A scene run directly (the editor, tools) keeps its own exports; `--ai=` and `--ai-level=` override the menu.
- [built] **Pause menu:** Esc when there's nothing to cancel, or F10. The game stops (the camera still moves) and the world takes no clicks; Resume, Restart, Main menu, Quit.
- [built] **Game over:** a banner with the winner (or a draw) and the game time, Restart, Main menu and Quit. The world keeps running behind it.
- [built] Order paths show only for selected units, colored by what the order does: green move, orange attack-move, red attack (a unit, a truck or a road piece), blue repair.
- [built] The cursor shows what a click will do, computed by the same code that issues the order. Round cursors click at their center; tool cursors point up-left, as on Windows.
- [built] The camera pans with the arrow keys (A is taken by attack-move), screen edges and middle-drag, and zooms with the wheel. It runs on real time, unaffected by game speed.
- [built] Game speed is 1x, 1.5x, 2x or 3x. The sim always runs 20 ticks per sim-second, so speed never changes results.
- [built] F2 hotseat: each side keeps its own camera. Swapping glides to that side's last view, or its spawn on its first visit. Off in a match against the AI started from the menu; on in hotseat and in scenes run directly (a test tool).
- [decided] The camera is perspective, with pitch 55-60° and field of view 35-40°. Lock it before serious modeling.

## Factions (working titles; final names are Denys's)

[decided] Their supply rules (gathering, "Belt" lines) were written for the belt. Revisit them for trucks on roads before any faction is built.

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

- [decided] One system: infrastructure objects with states (intact, damaged, broken) that can be repaired or improved. Route road pieces are the first user [built]; other roads and bridges reuse it.
- [built] **Road types (2026-10-01):** a route's pieces are dirt tracks or paved road. Bridges only over impassable terrain, never over roads, so navigation stays single-layer.
  - **Paved** pieces take 3× a dirt piece's damage to break (300 health against 100: about ten artillery shells), and units on paved road move faster: vehicles ×1.3, infantry ×1.1 (not on a broken piece). Pathfinding doesn't seek roads out yet: the speed is there when the way runs along one.
  - **Paving:** a builder or engineer right-clicks an intact dirt piece (damaged is fine, broken is repaired first) and paves it: 3 packages and 6 s a piece, paid as it works, stalling while its owner is broke; the damage it had stays. Roads are neutral, so the enemy's trucks and units use your paving too: an investment the enemy can use or has to siege.
  - **On the main map** each route is paved from its start through the crossing, and a dirt track along the contested tail: the rich depots out there break easily (Playable map).
  - **Looks:** paved is asphalt with painted lines; dirt is packed earth with two wheel ruts and no paint. The hover hint names the road and offers paving.
  - Numbers: `PavedHealth`, `PavedVehicleBoost`, `PavedFootBoost`, `PaveCost`, `PaveSeconds` in `Simulation.cs`; a map sets each route's paved stretch (`BeltPath.PavedTo`).
- [built] **Repair** is what builders and engineers do, per unit type (`repairSeconds`, `repairCost`); other units can't. The engineer (barracks, unarmed) repairs but doesn't build, quicker and cheaper than the builder.
  - It costs money and time: a unit repairs at its own rate, paying as the piece heals, and stalls while its owner is broke.
  - **Auto-repair:** an idle repairer fixes damaged open road within a small radius on its own, while its owner has packages. An order sets priorities.
  - [built] **What each type repairs is a list:** builders and engineers repair road and, right-clicking one, your own damaged buildings, depots and defenses (from 0 to full in half its build time, at least 5 s, for half its cost, paid as it goes); units mend only at home (Units: Squads). Medics may later heal infantry in the field through the same code. A full repair costs a share of the target's own cost, set per repairer type.
- [built] Buildings snap to a 2 m grid, rotated in 90° steps; navigation's cells are half that (1 m), so every building edge is a cell edge.
- [built] **Construction prototype** (generic, before factions):
  - A builder (unarmed, trained at the HQ) walks to the site and lays the foundation on arrival, if the spot is still clear and its owner has the building's whole cost in hand. Placing it is refused up front for the same reasons, and the card says which. The foundation grows only while a builder works on it; more builders don't speed it up, but any of yours can take over an abandoned one.
  - Cost is paid as it grows and it stalls if the money runs out after it started, like production. A foundation starts at a tenth of its health and is a target.
  - Types: barracks, factory, depot (it must stand beside a road), and two defenses, which become guns that can't move:
    - [built] **Turret** (needs a barracks): a machine gun, cheap. It shreds infantry; tanks outrange it.
    - [built] **Heavy turret** (needs a factory): an anti-tank cannon that outranges tanks, dearer. Rockets beat it (armored, so rifles barely scratch it). Tests hold the line: it stops a lone tank and three rifle squads, and loses to two rocket squads; the plain turret stops two rifle squads and loses to a tank.
    - [built] **Prerequisites** (`requires` in buildings.json): a finished building of that type. The command card greys out the cell and its tooltip says what's missing. A foundation already laid carries on if the prerequisite is lost.
    - Chosen over a researched upgrade (anti-tank shells for every turret, bought at the factory): two buildings put the choice where each turret goes, and need no research system.
  - [built] **Defenses and orders:** they take only attack orders, and drop one once the target is out of range or out of sight (they can't follow), then shoot whatever comes in range again. Selected, and while being placed, they show their range ring.
  - Placement: anywhere on the map that's clear of buildings, depots, defenses, rocks and roads. Depots also keep the depot spacing. A depot snaps beside the nearest open road near the cursor, facing it, and slides along it as the cursor moves; away from a road it can't be placed.
  - **Depot guidance (like RA3's refinery spots):** while a depot is being placed, green strips beside the roads mark every free stretch, and a ghost over a spot that's too close to another depot jumps along the road to the nearest free one (within the spacing).
  - Known risk of "anywhere": a depot on the enemy's home stretch, or a turret beside the enemy's home depot, in minute one. Acceptable for the prototype.
- [decided] **Factions replace "anywhere" with their own build-area rules** (see Factions): Western relays extend the build area, Asian foundations need reinforcing, Eastern civilian buildings go only near supply routes or in existing rural areas. This removes minute-one exploits and makes players plan their way out of the start.
- [proposed] Route buildings (depots, relays) snap to sockets beside road pieces. Depots snap to the road already [built], continuously rather than to fixed sockets; sockets are for when relays exist.
- [built] **Playable map** (`main.tscn`), generated by `tools/make_main_map.py`: 256×180 m, flat, with rocks. Its layout rules, which any map should follow:
  1. **Point-symmetric** (Blue's half turned 180° is Red's), so it's fair by construction.
  2. **One route per side,** its open road starting in front of its owner's base. Its **home stretch** is shorter than the depot spacing, so exactly one safe depot fits: the smallest share of every truck, yours as long as you defend it. It runs parallel to the center line (the line equally far from both HQs), so it stays equally safe along its length.
  3. **Then it jogs into the middle,** where the rest is up for grabs: your second depot goes there, exposed, and the rest flows on to slots worth more still. An enemy depot on your route has to stand upstream of yours to unload first, so each side is stronger on its own route, and symmetry evens it out.
  4. **The routes meet in the middle,** close enough (about 17 m) that raiding one puts you next to the other: one front. A band where both run side by side can't also give each a safe home stretch (a band's ends are equally far from both HQs, so a home stretch can't start there), so they meet at one point instead.
  5. **Past the middle, each route follows the center line out to a flank** and ends at the map edge, equally far from both HQs. Every slot there is contested, and worth the most (a depot's share grows down the route); what's left at the end goes back to the source, not to anyone.
  6. **Build space** around each HQ stays clear of roads, and both sides are equally far from the middle.
  7. **Rocks** (solid: nothing crosses or builds on them) shape the open ground: a ring of four round the middle, where the routes cross, with lanes between them, and one on each flank beside a route's tail. They keep 8 m from any road (depots, spills and repairs are never in them), 40 m from each HQ (room to build), and 10 m from each other (every lane takes a tank).
  8. **Paved home, dirt tail:** each route is paved road from its start to 15 m past the crossing along the center line, and dirt beyond (Road types).
  9. **Two garrison houses at the crossing,** one either side of the center line, each within a squad's reach of both roads (12 m from its middle), 8 m from any road and rock: every raid passes them, so holding one is a fight. (Beside the tails they were useless: nobody fought there.)
  10. **The generator checks it:** it prints how far each point along each route leans toward either HQ, and refuses to write the map unless each route's safe stretch fits one depot only, everything past its second slot is contested, its end is neutral, the routes stay apart, the HQs keep their build space, and the rocks and houses keep their distances.
  - Routes come in covered from beyond the map edge through each owner's back field and open where the home stretch starts; past the flank they're covered again out to beyond the edge. The generator's rules count open road only, and it checks that each route starts and ends beyond the map.
  - No prebuilt depots: players build them.
- [proposed] **Income vs spending:** full production of the dearest unit costs more than a side's income from its home depot alone, so extra income always buys something. Costs live in `data/units.json`.

## Win and lose (skirmish)

[built] A player loses when they can no longer rebuild: **no buildings, and either no builders or not enough money for the cheapest building.** A 60 s grace timer runs while there are no buildings but recovery is still possible. It's one function, `CanStillRecover(player)`, run every tick, backed by a unit-test table of edge cases. What's built, generic before factions:

- **Buildings** are finished production buildings (HQ, barracks, factory). Depots, turrets and held houses don't count: a side with only those can't train anything and can't come back (playtest: the AI lived on for minutes with two depots, no HQ and no builder). Foundations don't count either: they only pause the clock.
- **Money for the cheapest building:** the cheapest non-turret building a living builder can put up, or what's still owed on one of your own foundations, whichever is less. Paying into a foundation never makes you lose.
- **The clock** pauses only on ticks a builder is working a foundation (an abandoned one doesn't), resumes where it was when work stops, and clears once a building is finished.
- **Broke means out:** no buildings and not enough money loses at once, with no clock, even if income may still arrive.
- **Losing destroys everything the player has left** (units, turrets, depots, foundations), then the game-over banner shows. The game ends when at most one player is left; if the last ones go on the same tick, it's a draw.
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
  - Escalating stages: grumbling, theft from depots, road sabotage, militia.
  - The Eastern bloc profits from others' unrest.
  - Razing must cost the razer something.

## Look

- [decided] The environment is worn, desaturated and mid-contrast. Units have neutral bodies with saturated faction accents: silhouette first, color second. The routes, trucks and their cargo are the highest contrast on screen.
  - [built] **Sides (2026-10-01, from the first squint test: Red and Blue squads were the same mid-gray on the ground):** bodies are neutral and the same for both sides, khaki infantry and olive vehicles, lighter than the ground so units stand out from it in value; a side's color is on what the camera sees from above: helmets, turret-top plates, the builder's cab roof, and a roof plate on buildings and depots. A garrison house's walls take its holder's color.
  - [built] **Class silhouettes:** rifles held forward, a long launcher along each rocket trooper's shoulder (members face where they go), a big pack on the engineer; a long narrow car with a small gun, a wide tank with a long gun, artillery's long hull with its turret set back and a raised barrel, the builder's blade, cab and crane arm.
  - [built] **Health bars** show over whatever is damaged, and over whatever is selected, whole or not.
  - [proposed] Still to do: rocks are the brightest thing on screen (darker, lower-contrast rocks and a faint ground texture would let the routes pop), and nothing yet shows who's shooting whom beyond tracers.
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
2. **Route flow:** [built] a truck per source at a fixed interval, finite sources on the main map; cargo reaching an end goes back into its source's reserve (see Supply routes).
3. **Western rebuild fallback:** a mobile relay unit, or an airdropped first structure.
4. **When every source is dry:** the fight goes on with what's banked, and destruction decides. Revisit if matches stall. They can: the scripted AI sometimes ends up in mirror matches with both sides broke, every depot gone and too little left in the sources to matter, and neither attacks. Look at it again with the behaviour-tree AI and real playtests.
5. **Fog of war:** [built] grid-based over the navigation bounds (2 m cells), radius sight only (see Units: Fog of war).
6. **Baseline test machine:** undecided (Steam Deck or a mid-range laptop).
7. **Name:** working title *Triple Point*. [decided] The code is open source under Apache 2.0 (public on GitHub); art, audio, story and the name are not covered by it and are what a release sells. Code names stay neutral (`Game.sln`, assembly `Game`).
8. **Tax and unrest balance, final faction names:** later, Denys's call.
9. **What makes the routes worth fighting over** (first playtest: the belt wasn't). [built, to playtest] Trucks on roads (Supply routes): depots further down the route take more, a break holds trucks where they can be shot, and a shot truck spills for whoever is there.
10. **The slow start** (first playtest): about 75 s before the first package arrived. [built] Routes start full.

## Rejected (don't re-propose without a new reason)

- **A repeat toggle for production** (built, removed 2026-09-30): useless once queues are long and Shift queues five; one way to keep a building busy is enough.
- **Healing without an order** (squads refilling near base on their own) **and engineers repairing vehicles in the field** (2026-09-30): mending means bringing units home to the barracks or factory, Generals-style, so a raid that goes wrong costs the trip back.

- **Two currencies:** they clutter the cargo, the one thing that must read instantly, and double the cost tuning across three factions. High-value packages give the "which to raid" choice more cheaply.
- **Conveyor belts** as the carrier (built, then replaced by supply trucks on 2026-09-30): a belt had no reason to exist in the world, and with a post taking half its flow a side's own two posts drained its belt at home, so the contested middle carried nothing and nobody fought over it. Trucks are neutral traffic you can hold up, ambush and escort.
- **Capture points** (switches taken by holding a circle for a few seconds): timer-based control from Relic games; it feels artificial. Control comes from depots that survive, road that breaks and trucks that get shot.
- **Junctions** (merges and switches): they complicated the belt without adding a decision posts and breaks don't already give. Every route is one line from a source to an end; so no loops either.
- **Splash damage on tank shells:** tanks would be best at everything; splash belongs to artillery and rockets.
- **Turrets tracking enemies out of range, with no order:** too twitchy. Only an attack order aims ahead, and a hit makes the turret watch its attacker for a while.
- **Upfront payment for production (Generals style):** repeat would wait until the full cost was banked, and it hides how income becomes units.
- **Units that stop firing while moving:** units fire on the move; only a future per-type flag for heavy or emplaced weapons.
- **Tween curves for vehicle motion:** easing is physics, so it reacts to new orders mid-move.
- **Navmesh, TOML game data, TAA, ECS (for now):** reasons in `docs/architecture.md`.
