using System.Collections.Generic;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Draws buildings: a block in the owner's colors with a door on its exit side, a health bar once
/// damaged, and a production bar while it trains a unit (amber while stalled for Resources). The
/// selected building gets an outline and a flag on its rally point. Placeholder look, built in code.
/// </summary>
public partial class BuildingsView : Node3D
{
    const float BlockHeight = 3f;
    static readonly Color Working = new(0.85f, 0.85f, 0.9f), Stalled = new(0.95f, 0.6f, 0.15f);

    [Export] public StandardMaterial3D BuildingMaterial = null!;
    [Export] public Material? IndicatorMaterial; // outline and rally flag, like the switch arrows

    sealed record View(Node3D Root, HealthBar Health, HealthBar Production, MeshInstance3D Outline)
    {
        public bool Selected;
    }

    readonly Dictionary<int, View> _views = []; // by building id; destroyed buildings go away
    readonly Dictionary<int, StandardMaterial3D> _materials = []; // per player
    readonly HashSet<int> _seen = [];
    readonly List<int> _gone = [];
    Node3D _rally = null!;

    public override void _Ready()
    {
        _rally = new Node3D { Name = "Rally", Visible = false };
        _rally.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 2f }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0, 1, 0) });
        _rally.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.45f, 0.05f) }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0.38f, 1.75f, 0) });
        AddChild(_rally);
    }

    public void Sync(SimState state, int selected)
    {
        _seen.Clear();
        Building? shownRally = null;
        foreach (var building in state.Buildings)
        {
            _seen.Add(building.Id);
            if (!_views.TryGetValue(building.Id, out var view)) _views[building.Id] = view = Build(building);

            float health = building.Health / building.MaxHealth;
            view.Health.Set(health, HealthBar.HealthColor(health));
            view.Health.Visible = health < 1;

            bool producing = building.Queue.Count > 0;
            if (producing) view.Production.Set((float)building.Progress / building.Queue[0].BuildTicks, building.Stalled ? Stalled : Working);
            view.Production.Visible = producing;

            bool isSelected = building.Id == selected;
            if (view.Selected != isSelected) view.Outline.Visible = view.Selected = isSelected;
            if (isSelected) shownRally = building;
        }

        _rally.Visible = shownRally is not null;
        if (shownRally is not null) _rally.Position = ToGodot(shownRally.Rally);

        if (_views.Count == _seen.Count) return;
        _gone.Clear();
        foreach (int id in _views.Keys)
            if (!_seen.Contains(id)) _gone.Add(id);
        foreach (int id in _gone)
        {
            _views[id].Root.QueueFree();
            _views.Remove(id);
        }
    }

    View Build(Building building)
    {
        float size = building.Type.Size;
        var root = new Node3D { Position = ToGodot(building.Position), Rotation = new Vector3(0, building.Heading, 0) };
        AddChild(root);
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size, BlockHeight, size) }, MaterialOverride = MaterialFor(building.Owner), Position = new Vector3(0, BlockHeight / 2, 0) });
        // The door: a dark slab on the exit side (the building's +z, along its heading).
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size * 0.4f, BlockHeight * 0.55f, 0.2f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.11f) }, Position = new Vector3(0, BlockHeight * 0.275f, size / 2) });
        var outline = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size + 0.6f, 0.08f, size + 0.6f) }, MaterialOverride = IndicatorMaterial, Position = new Vector3(0, 0.04f, 0), Visible = false };
        root.AddChild(outline);

        var health = new HealthBar { Position = new Vector3(0, BlockHeight + 0.9f, 0), Visible = false };
        var production = new HealthBar { Position = new Vector3(0, BlockHeight + 0.5f, 0), Visible = false };
        root.AddChild(health);
        root.AddChild(production);
        return new View(root, health, production, outline);
    }

    StandardMaterial3D MaterialFor(int player)
    {
        if (_materials.TryGetValue(player, out var material)) return material;
        material = (StandardMaterial3D)BuildingMaterial.Duplicate();
        material.AlbedoColor = BuildingMaterial.AlbedoColor.Lerp(PlayerPalette.Color(player), 0.55f);
        return _materials[player] = material;
    }
}
