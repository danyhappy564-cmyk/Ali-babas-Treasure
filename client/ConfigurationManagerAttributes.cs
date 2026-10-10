// ReSharper disable UnusedMember.Global
namespace AlibabaTreasure.Client;

/// <summary>
/// The drop-in class BepInEx's ConfigurationManager (the F12 window) looks for by name. It is never
/// referenced as a type - the fields are read reflectively, each time the window opens - so changing
/// <see cref="Category"/>/<see cref="DispName"/>/<see cref="Description"/> after binding is how the
/// window switches language (kb 10.9). Only the members this mod sets are declared.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>Higher numbers sort earlier inside a section.</summary>
    public int? Order;

    /// <summary>Section title shown in the window (overrides the .cfg section name).</summary>
    public string? Category;

    /// <summary>Setting name shown in the window (overrides the .cfg key).</summary>
    public string? DispName;

    /// <summary>Tooltip shown in the window (overrides the ConfigDescription text).</summary>
    public string? Description;
}
