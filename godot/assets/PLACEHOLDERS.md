# Placeholder assets

Every AI-generated or otherwise temporary asset must be listed here, so nothing ships by accident. Steam requires disclosure of AI-generated content.

| Path | Kind | Source / tool | AI-generated | Replace before |
|------|------|---------------|--------------|----------------|
| `godot/scripts/Cursors.cs` | Mouse cursors (move, attack, attack-move, repair, garrison) | Drawn in code from simple shapes | No | Real cursor art |
| `godot/scripts/UnitsView.cs` | Weapon effects: tracer lines, shell slug, muzzle and impact flash balls; artillery shell smoke puffs and ground shadow disc; range rings | Built-in meshes and flat colors, in code | No | Real VFX |
| `godot/scripts/UnitsView.cs` | Vehicle wrecks: the unit's own meshes in a burnt material, a boom flash, smoke balls, sinking away | Built-in meshes and flat colors, in code | No | Real wreck models and explosion VFX |
| `godot/scripts/BuildingsView.cs` | Buildings (a colored block with a door; a garrison house with a gable roof and occupancy pips), selection outline, rally flag | Built-in meshes and flat colors, in code | No | Real building models |
| `godot/scripts/CommandCard.cs` | Command card: a 4×3 grid of default Godot buttons with text, a key label, a count badge and a thin progress bar | Godot's default theme and StyleBoxFlats, in code | No | Real UI with icons |
| `godot/scripts/SelectionPanel.cs` | Selection panel: a flat dark box, default font, default buttons as type tiles, flat health bars | Godot's default theme and StyleBoxFlats, in code | No | Real UI with unit portraits and icons |
| `godot/scripts/TopBar.cs` | Top strip: a flat dark rect; alerts as pulsing default-font text | Canvas drawing and default font, in code | No | Real UI |
| `godot/scripts/IdleBuilderButton.cs` | Idle-builder button: a default Godot button with text | Godot's default theme, in code | No | Real UI with an icon |
| `godot/scripts/BeltView.cs` | Roads: a flat asphalt strip with painted edge lines, a dashed center line and gravel verges; a dirt track (packed earth, two darker ruts); a box housing for covered stretches, torn slabs, a crater disc and debris boxes when broken, green depot-spot strips | Procedural meshes in code, vertex colors | No | Real road and terrain art |
| `godot/scripts/BeltView.cs` | Supply trucks (boxes: cab, bed, wheels, windscreen), crates on the bed, charred box wrecks; depots (a shed box and a loading-bay slab) | Procedural meshes and built-in boxes in code, vertex colors | No | Real truck and depot models, wreck VFX |
| `godot/scripts/BeltView.cs` | Source gauge (a bar and a Label3D), spill hop off a truck and tumble, shard burst, collection flight and +1 label | Built-in meshes, default font, in code | No | Real UI and VFX |
| `godot/assets/belt.png`, `godot/assets/supplies.png` | Source sheets: belt parts, crate faces | ChatGPT (OpenAI image generation) | Yes | Real textures |
| `godot/assets/textures/belt_atlas.png`, `package_crates.png` | Atlases cut from the source sheets | `tools/make_textures.py` | Yes (from the sheets above) | Real textures |
| `godot/scripts/Minimap.cs` | Minimap: flat colored lines, dots, squares and rock shapes | Canvas drawing in code | No | Real minimap art |
| `godot/scripts/FogOverlay.cs` | Fog of war: a flat dark-blue shade over unseen ground, blurred a cell, and the same shade on the minimap; the belt-cut alert rings on the minimap | Shader and canvas drawing in code | No | Real fog look (clouds or shroud) and alert UI |
| `godot/scripts/MapObstacle.cs` | Rocks: a flat grey box each | Built-in mesh and flat color, in code | No | Real rock models |
| `godot/scripts/PackagePanel.cs` | Packages panel: a flat dark box with a player-colored edge, default font | Godot's default theme and a StyleBoxFlat, in code | No | Real UI |
| `godot/scripts/GameOverOverlay.cs` | Game-over banner: a flat dark box, default font and buttons | Godot's default theme and a StyleBoxFlat, in code | No | Real UI |
| `godot/scripts/UnitView.cs` | Builder (hull with a blade), turret (squat base with a gun), heavy turret (a big turret and a thick barrel on the same base), artillery (long hull, raised heavy barrel), engineer (a single figure with a tool pack) and rocket squad (a launcher tube on each member) looks, like all unit looks | Built-in meshes, in code | No | Real models |
