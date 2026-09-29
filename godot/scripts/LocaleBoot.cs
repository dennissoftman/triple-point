using Godot;

/// <summary>An autoload: sets the language before any scene builds its text (see L).</summary>
public partial class LocaleBoot : Node
{
    public override void _EnterTree() => L.ApplyLanguage();
}
