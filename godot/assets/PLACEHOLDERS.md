# Placeholder assets

Every AI-generated or otherwise temporary asset must be listed here, so nothing ships by accident. Steam requires disclosure of AI-generated content.

| Path | Kind | Source / tool | AI-generated | Replace before |
|------|------|---------------|--------------|----------------|
| `godot/scripts/Cursors.cs` | Mouse cursors (move, attack, attack-move, repair) | Drawn in code from simple shapes | No | Real cursor art |
| `godot/scripts/UnitsView.cs` | Weapon effects: tracer lines, shell slug, muzzle and impact flash balls; artillery shell smoke puffs and ground shadow disc; range rings | Built-in meshes and flat colors, in code | No | Real VFX |
| `godot/scripts/UnitsView.cs` | Vehicle wrecks: the unit's own meshes in a burnt material, a boom flash, smoke balls, sinking away | Built-in meshes and flat colors, in code | No | Real wreck models and explosion VFX |
| `godot/scripts/BuildingsView.cs` | Buildings (a colored block with a door), selection outline, rally flag | Built-in meshes and flat colors, in code | No | Real building models |
| `godot/scripts/CommandCard.cs` | Command card: default Godot buttons and text | Godot's default theme, in code | No | Real UI |
| `godot/scripts/BeltView.cs` | Belts: extruded rails, base and joint bars, a box housing for covered stretches, wrecked halves and debris boxes, green post-spot strips | Procedural meshes in code, vertex colors | No | Real belt models, a scrolling surface shader |
| `godot/scripts/BeltView.cs` | Source gauge (a bar and a Label3D), spill hop and tumble, shard burst, collection flight and +1 label | Built-in meshes, default font, in code | No | Real UI and VFX |
| `godot/scripts/Minimap.cs` | Minimap: flat colored lines, dots and squares | Canvas drawing in code | No | Real minimap art |
| `godot/scripts/ResourcePanel.cs` | Resources panel: a flat dark box with a player-colored edge, default font | Godot's default theme and a StyleBoxFlat, in code | No | Real UI |
| `godot/scripts/GameOverOverlay.cs` | Game-over banner: a flat dark box, default font and buttons | Godot's default theme and a StyleBoxFlat, in code | No | Real UI |
| `godot/scripts/UnitView.cs` | Builder (hull with a blade), turret (squat base with a gun), artillery (long hull, raised heavy barrel) and engineer (a single figure with a tool pack) looks, like all unit looks | Built-in meshes, in code | No | Real models |
