using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws buildings: a neutral block with a roof plate in the owner's color (a production building gets a door on its exit side),
/// a health bar once damaged, and a progress bar while it trains a unit or, as a foundation, while it's
/// built (amber while stalled for packages). A foundation rises as it's built. The selected building
/// gets an outline and a flag on its rally point, and a building being placed shows as a ghost, green
/// where it fits and red where it doesn't. Under fog of war an enemy building shows live while seen, as
/// last seen while remembered (even once it's gone, until the player looks again), and not at all before
/// it's been seen. A garrison building (a house) is part of the map, so it always shows: walls in the
/// color of whoever holds it (gray while nobody does; under fog, as last seen), a roof, and a pip on the
/// roof ridge per place inside, lit for each squad in it. Placeholder look, built in code.
/// </summary>
public partial class BuildingsView : Node3D
{
    const float BlockHeight = 3f, LowBlockHeight = 1.4f; // production buildings; posts and defenses
    const float WallHeight = 2.4f, RoofHeight = 1.3f;     // a garrison building's walls and the roof on them
    const float RoofPlate = 0.1f;                         // m: the owner-colored plate on other buildings' roofs
    const float MinFoundation = 0.1f;                     // share of full height a new foundation shows
    static readonly Color Working = new(0.85f, 0.85f, 0.9f), Stalled = new(0.95f, 0.6f, 0.15f);
    static readonly Color Fits = new(0.4f, 1, 0.5f, 0.35f), Blocked = new(1, 0.3f, 0.25f, 0.35f);
    static readonly StandardMaterial3D Roof = new() { AlbedoColor = new Color(0.36f, 0.24f, 0.2f) };
    static readonly StandardMaterial3D PipEmpty = new() { AlbedoColor = new Color(0.12f, 0.12f, 0.13f) };

    [Export] public StandardMaterial3D BuildingMaterial = null!;
    [Export] public Material? IndicatorMaterial; // outline and rally flag, like the switch arrows

    sealed record View(Node3D Root, Node3D Block, MeshInstance3D Body, HealthBar Health, HealthBar Progress, MeshInstance3D Outline, MeshInstance3D[] Pips)
    {
        public bool Selected;
        public float Height = -1; // share of full height, as drawn
        public int Owner = int.MinValue, Occupants = -1; // a garrison building's, as drawn
    }

    readonly Dictionary<int, View> _views = []; // by building id; destroyed ones go away
    readonly Dictionary<int, StandardMaterial3D> _materials = []; // per player
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    readonly StandardMaterial3D _ghostMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = Fits,
    };
    readonly BoxMesh _ghostMesh = new();

    // The construction grid around a building being placed: world-space lines every grid cell, fading out
    // with distance from the ghost. One quad that follows the ghost; the shader draws the lines.
    const float GridRadius = 18f; // m
    const string GridShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec4 line_color : source_color = vec4(0.85, 0.95, 0.9, 0.35);
uniform float cell = 2.0;
uniform float radius = 18.0;
varying vec3 world;
void vertex() { world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
    vec2 g = world.xz / cell;
    vec2 d = abs(fract(g - 0.5) - 0.5) / fwidth(g); // pixels from the nearest line, each way
    float line = 1.0 - min(min(d.x, d.y), 1.0);
    float fade = 1.0 - smoothstep(radius * 0.4, radius, distance(world.xz, NODE_POSITION_WORLD.xz));
    ALBEDO = line_color.rgb;
    ALPHA = line_color.a * line * fade;
}";
    MeshInstance3D _grid = null!;
    bool _gridShown;
    Node3D _rally = null!;
    MeshInstance3D _ghost = null!, _ghostExit = null!; // the exit marker shows which way it faces
    (string Type, bool Valid) _ghostShown;

    public override void _Ready()
    {
        _rally = new Node3D { Name = "Rally", Visible = false };
        _rally.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 2f }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0, 1, 0) });
        _rally.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.45f, 0.05f) }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0.38f, 1.75f, 0) });
        AddChild(_rally);
        AddChild(_ghost = new MeshInstance3D { Name = "Ghost", Mesh = _ghostMesh, MaterialOverride = _ghostMaterial, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _ghost.AddChild(_ghostExit = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.2f, 0.1f, 0.6f) }, MaterialOverride = IndicatorMaterial });
        var grid = new ShaderMaterial { Shader = new Shader { Code = GridShader } };
        grid.SetShaderParameter("cell", Simulation.GridCell);
        grid.SetShaderParameter("radius", GridRadius);
        AddChild(_grid = new MeshInstance3D
        {
            Name = "Grid",
            Mesh = new PlaneMesh { Size = new Vector2(2 * GridRadius, 2 * GridRadius) },
            MaterialOverride = grid,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    public void Sync(SimState state, int selected, PlayerInput.Placement? placing)
    {
        _seen.Clear();
        Building? shownRally = null;
        foreach (var building in state.Buildings)
        {
            _seen.Add(building.Id);
            if (!_views.TryGetValue(building.Id, out var view)) _views[building.Id] = view = Build(building);

            bool live = Sight.SeesStructure(building.Owner, building.Position, building.Type.Size / 2);
            if (!live)
            {
                if (!ShowRemembered(building.Id, view) && building.Type.Kind == BuildingKind.Garrison)
                {
                    view.Root.Visible = true; // never seen: part of the map all the same, and nobody's as far as anyone knows
                    ShowHolder(view, Player.None, 0);
                }
                continue;
            }
            view.Root.Visible = true;
            float health = building.Health / building.MaxHealth;
            view.Health.Set(health, HealthBar.HealthColor(health));
            view.Health.Visible = building.Built && (health < 1 || building.Id == selected); // damaged, or selected

            float height = building.Built ? 1 : Mathf.Max(MinFoundation, (float)building.BuildProgress / building.Type.BuildTicks);
            if (height != view.Height) view.Block.Scale = new Vector3(1, view.Height = height, 1);

            if (!building.Built) view.Progress.Set((float)building.BuildProgress / building.Type.BuildTicks, building.BuildStalled ? Stalled : Working);
            else if (building.Queue.Count > 0) view.Progress.Set((float)building.Progress / building.Queue[0].BuildTicks, building.Stalled ? Stalled : Working);
            view.Progress.Visible = !building.Built || building.Queue.Count > 0;

            if (building.Type.Kind == BuildingKind.Garrison) ShowHolder(view, building.Owner, building.Occupants);

            bool isSelected = building.Id == selected;
            if (view.Selected != isSelected) view.Outline.Visible = view.Selected = isSelected;
            if (isSelected && building.Built && building.Type.Units.Length > 0) shownRally = building;
        }

        _rally.Visible = shownRally is not null;
        if (shownRally is not null) _rally.Position = ToGodot(shownRally.Rally);
        SyncGhost(placing);

        if (_views.Count == _seen.Count) return;
        _gone.Clear();
        foreach (var (id, view) in _views)
            if (!_seen.Contains(id) && !ShowRemembered(id, view)) _gone.Add(id); // a gone one stays while remembered
        foreach (int id in _gone)
        {
            _views[id].Root.QueueFree();
            _views.Remove(id);
        }
    }

    // An enemy building out of sight: as the local player last saw it, if it remembers it at all. False if not.
    bool ShowRemembered(int id, View view)
    {
        bool remembered = Sight.Remembers(id, out var ghost);
        view.Root.Visible = remembered;
        view.Health.Visible = view.Progress.Visible = false;
        if (view.Selected) view.Outline.Visible = view.Selected = false;
        if (remembered && ghost.Type is not null)
        {
            float height = ghost.Built ? 1 : MinFoundation;
            if (height != view.Height) view.Block.Scale = new Vector3(1, view.Height = height, 1);
            if (ghost.Type.Kind == BuildingKind.Garrison) ShowHolder(view, ghost.Owner, ghost.Owner == Player.None ? 0 : -1);
        }
        return remembered;
    }

    // A garrison building's walls in its holder's color, and a lit pip per squad inside (-1: not known,
    // all dark, as remembered).
    void ShowHolder(View view, int owner, int occupants)
    {
        if (owner != view.Owner) view.Body.MaterialOverride = MaterialFor(view.Owner = owner);
        if (occupants == view.Occupants) return;
        view.Occupants = occupants;
        for (int i = 0; i < view.Pips.Length; i++) view.Pips[i].MaterialOverride = i < occupants ? PipMaterialFor(owner) : PipEmpty;
    }

    void SyncGhost(PlayerInput.Placement? placing)
    {
        _ghost.Visible = placing is not null;
        bool grid = placing is { Type.Kind: not BuildingKind.Post }; // posts snap to the belt, not the grid
        if (grid != _gridShown) _grid.Visible = _gridShown = grid;
        if (placing is not PlayerInput.Placement p) return;
        if (grid) _grid.Position = ToGodot(p.At) with { Y = 0.03f };
        float height = HeightOf(p.Type);
        if (_ghostShown.Type != p.Type.Id)
        {
            _ghostMesh.Size = new Vector3(p.Type.Size, height, p.Type.Size);
            _ghostExit.Position = new Vector3(0, -height / 2 + 0.05f, p.Type.Size / 2 + 0.5f);
        }
        if (_ghostShown.Valid != p.Valid || _ghostShown.Type != p.Type.Id) _ghostMaterial.AlbedoColor = p.Valid ? Fits : Blocked;
        _ghostShown = (p.Type.Id, p.Valid);
        _ghost.Position = ToGodot(p.At) + new Vector3(0, height / 2, 0);
        _ghost.Rotation = new Vector3(0, p.Heading, 0);
    }

    static float HeightOf(BuildingType type) => type.Kind switch
    {
        BuildingKind.Building => BlockHeight,
        BuildingKind.Garrison => WallHeight + RoofHeight,
        _ => LowBlockHeight,
    };

    View Build(Building building)
    {
        float size = building.Type.Size, height = HeightOf(building.Type);
        var root = new Node3D { Position = ToGodot(building.Position), Rotation = new Vector3(0, building.Heading, 0) };
        AddChild(root);
        // The block scales up from the ground as a foundation is built.
        var block = new Node3D();
        root.AddChild(block);
        bool house = building.Type.Kind == BuildingKind.Garrison;
        float walls = house ? WallHeight : height;
        // Neutral walls with the owner's color on the roof, seen from above; a house's walls take its holder's.
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size, walls, size) }, MaterialOverride = house ? MaterialFor(building.Owner) : BuildingMaterial, Position = new Vector3(0, walls / 2, 0) };
        block.AddChild(body);
        if (!house) block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size * 0.86f, RoofPlate, size * 0.86f) }, MaterialOverride = AccentFor(building.Owner), Position = new Vector3(0, walls + RoofPlate / 2, 0) });
        var pips = new MeshInstance3D[house ? building.Type.Garrison : 0];
        if (house)
        {
            // A gable roof, its ridge running toward the exit side, and the pips along the ridge.
            block.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(size + 0.4f, RoofHeight, size + 0.4f) }, MaterialOverride = Roof, Position = new Vector3(0, walls + RoofHeight / 2, 0) });
            for (int i = 0; i < pips.Length; i++)
                block.AddChild(pips[i] = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.45f, 0.45f, 0.45f) },
                    MaterialOverride = PipEmpty,
                    Position = new Vector3(0, walls + RoofHeight + 0.35f, (i - (pips.Length - 1) / 2f) * 0.8f),
                });
            // A door on the exit side, as production buildings have.
            block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size * 0.3f, walls * 0.6f, 0.2f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.11f) }, Position = new Vector3(0, walls * 0.3f, size / 2) });
        }
        if (building.Type.Units.Length > 0) // the door: a dark slab on the exit side (the building's +z, along its heading)
            block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size * 0.4f, height * 0.55f, 0.2f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.11f) }, Position = new Vector3(0, height * 0.275f, size / 2) });
        var outline = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size + 0.6f, 0.08f, size + 0.6f) }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0, 0.04f, 0), Visible = false };
        root.AddChild(outline);

        var health = new HealthBar { Position = new Vector3(0, height + 0.9f, 0), Visible = false };
        var progress = new HealthBar { Position = new Vector3(0, height + 0.5f, 0), Visible = false };
        root.AddChild(health);
        root.AddChild(progress);
        return new View(root, block, body, health, progress, outline, pips);
    }

    readonly Dictionary<int, StandardMaterial3D> _pipMaterials = [], _accents = []; // per player

    // A building's roof plate: its owner's color, saturated.
    StandardMaterial3D AccentFor(int player)
    {
        if (_accents.TryGetValue(player, out var material)) return material;
        return _accents[player] = new StandardMaterial3D { AlbedoColor = PlayerPalette.Color(player), Roughness = 0.7f };
    }

    StandardMaterial3D PipMaterialFor(int player)
    {
        if (_pipMaterials.TryGetValue(player, out var material)) return material;
        return _pipMaterials[player] = new StandardMaterial3D
        {
            AlbedoColor = PlayerPalette.Color(player),
            EmissionEnabled = true,
            Emission = PlayerPalette.Color(player),
            EmissionEnergyMultiplier = 0.6f,
        };
    }

    StandardMaterial3D MaterialFor(int player)
    {
        if (_materials.TryGetValue(player, out var material)) return material;
        material = (StandardMaterial3D)BuildingMaterial.Duplicate();
        material.AlbedoColor = BuildingMaterial.AlbedoColor.Lerp(PlayerPalette.Color(player), 0.55f);
        return _materials[player] = material;
    }
}
