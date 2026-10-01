# Game data

The three files in `/data` are JSON with comments and trailing commas allowed, parsed by `src/Sim/GameData.cs`. Each is an object whose keys are ids (what other files and the maps refer to). A field not listed here is an error when the game loads, so a misspelling fails loudly instead of being ignored.

This page is checked by `DataSchemaTests`: every field the parser accepts must be in a table below, and every field in a table must exist. Change the record in `src/Sim/SimState.cs`, the file, and this page together.

Units: m, s, m/s, degrees per second, packages (the currency). Ticks are 1/20 s; times are rounded to ticks.

## weapons.json

Named by `weapon` in units.json.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `kind` | `bullet` or `shell` | required | A bullet hits the moment it fires (drawn as a tracer). A shell flies at `shellSpeed` and hits on arrival. |
| `damage` | number | required | Per shot; for a squad, per member. A direct hit on a squad fells one member at most: big guns waste most of a hit on infantry. |
| `reload` | s | required | Between shots. Above 0. |
| `range` | m | required | How far it reaches. Above 0. |
| `minRange` | m | 0 | It won't pick or fire at anything closer. Below `range`. |
| `shellSpeed` | m/s | 0 | Shells only, and above 0 for them. |
| `hit` | `direct` or `splash` | `direct` | Direct damages only the target, and never road. Splash damages every enemy unit, post and building within `splashRadius` of the impact, and every open belt segment there (anyone's), falling to half at the edge; only splash breaks road, so only splash weapons take road orders; a squad takes it on the share of its footprint the blast covers. No friendly fire on units, posts or buildings. |
| `splashRadius` | m | 0 | Above 0 exactly when `hit` is `splash`. |
| `ballistic` | bool | false | Shells only. It flies in an arc to where the target stood when it fired and bursts there, instead of homing on the target. |
| `scatter` | m | 0 | Shells only. How far off the aim point a ballistic shell may land at full range, less closer in, anywhere around it. |
| `prefers` | `any`, `infantry` or `vehicle` | `any` | Which units it picks first when several are in range: infantry is anything on foot, vehicles everything else (defenses too). Within that class, and among the rest after it, the weakest. Units always come before depots and buildings. |
| `against` | `{ "infantry", "vehicle", "structure" }`, factors | each 1 | Armor, from the gun's side: times its damage on infantry (anything on foot), vehicles (everything else, defenses too) and structures (buildings, foundations, depots). Leave a class out for 1. Small arms barely scratch vehicles (×0.15–0.2), siege guns wreck structures (artillery ×3). Trucks and road take its damage as it is. At least 0 each. |

## units.json

Named by `produces` and `unit` in buildings.json, and by `UnitType` on a map's unit spawns.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `members` | integer | required | 1 for a vehicle or a lone soldier; more for a squad, which is one entity whose members die one by one. |
| `speed` | m/s | required | Top speed. 0 for a defense. |
| `memberHealth` | number | required | Health per member; the unit's is this times `members`. |
| `weapon` | weapon id | none | Its gun, from weapons.json. None: unarmed. |
| `movement` | `foot`, `wheeled`, `tracked` or `static` | `foot` | How it drives. Static: never moves (defenses, built from a foundation). |
| `acceleration` | m/s² | 0 | Vehicles: at full throttle. |
| `braking` | m/s² | twice `acceleration` | Vehicles: at full brake. |
| `easeIn` | s | 0 | Vehicles: how long throttle, brake and steering take to build up to full. 0 is instant, and looks like a toy. |
| `easeOut` | s | 0 | Vehicles: how long they take to settle again. |
| `turnRate` | °/s | 0 | Vehicles: how fast the hull turns. |
| `turretTurnRate` | °/s | 0 | How fast the turret turns; 0: no turret, it aims instantly. A turret fires once it's on target. |
| `turretArc` | ° | 0 | How far the turret turns either side of the hull's nose; 0: all the way round. For a target beyond it, a unit that isn't driving anywhere pivots its hull toward it at `turnRate`. |
| `reverseSpeed` | m/s | 0 | Vehicles: how fast it backs up; 0: it always turns around. |
| `cost` | packages | 0 | Paid bit by bit over `buildTime` while a building produces it; production stalls while broke. |
| `buildTime` | s | 0 | How long it takes to produce. |
| `builds` | building ids | none | What it can construct (a builder). None: it doesn't build. |
| `repairSeconds` | s | 0 | It repairs belt, taking this long for a segment from 0 to full health. 0: it doesn't repair. An idle one repairs damaged belt close by on its own. |
| `repairCost` | packages | 0 | What a full repair of a segment costs it, paid as it goes; repair stalls while broke. 0 with `repairSeconds`: free. |
| `stopsToFire` | bool | false | It only fires while standing still, never on the move (artillery). |
| `radius` | m | 0.5 | The room it takes on the ground: paths keep its middle this far from anything solid (buildings, posts, defenses, obstacles, the map edge), so it only fits through gaps twice as wide, and two units push apart until their circles of this radius no longer overlap. With navigation off (test maps), it does nothing. |
| `sight` | m | 20 | How far it sees under fog of war: enemy units show to its side within this of it. Keep it above its weapon's range, or it needs another unit to spot for it (artillery does, on purpose). |

## buildings.json

Named by `builds` in units.json, and by `BuildingType` on a map's building spawns.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `health` | number | required | Full health. Above 0. |
| `size` | m | required | The side of its square footprint, snapped to the 2 m grid. |
| `produces` | unit ids | none | What it trains, in the order the command card shows them. |
| `cost` | packages | 0 | Paid bit by bit over `buildTime` while a builder works on the foundation; starting one needs the whole cost in hand, and it stalls if the money runs out after that. |
| `buildTime` | s | 0 | How long a builder takes to put it up. |
| `kind` | `building`, `post`, `defense` or `garrison` | `building` | What it becomes when finished: stays a building (and may produce units); becomes a gatherer post (it must stand beside open belt); becomes the unit named by `unit`, a static defense. A garrison is a neutral building placed on the map (nobody builds one) that infantry squads go into: whoever is inside owns it, fires out of it and can't be hit; the building takes the hits, and its squads come out at half health when it falls. |
| `garrison` | integer | 0 | A garrison: how many infantry squads it holds; above 0 exactly for that kind, so big and small buildings are just different types. |
| `unit` | unit id | none | A defense: the unit it becomes. |
| `sight` | m | 10 | How far beyond its footprint's edge it sees under fog of war (a finished post keeps it; a finished defense sees as its unit does). |
| `mends` | `infantry` or `vehicle` | none | Its owner's units of that class it heals when sent to it (right-click it): barracks infantry, factory vehicles. A squad regains its lost members as it heals. |
| `mendSeconds` | s | 20 | How long a mend from 0 to full health takes. |
| `mendShare` | factor | 0.5 | Share of the unit's cost a mend from 0 to full costs, paid as it heals; it stalls while broke. |
| `requires` | building id | none | A finished building of this type its owner needs before starting one (a foundation already laid carries on if it's lost). |
