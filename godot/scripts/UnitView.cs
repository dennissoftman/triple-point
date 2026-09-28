using Godot;

/// <summary>The visual for one unit. Only knows how to look; UnitsView positions it.</summary>
public partial class UnitView : Node3D
{
    [Export] public Node3D SelectionRing = null!;

    bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            SelectionRing.Visible = value;
        }
    }
}
