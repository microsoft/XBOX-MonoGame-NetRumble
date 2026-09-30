namespace NetRumble.Core.Simulation;

/// <summary>
/// The five collision layers, ported from the <c>[layer_names]</c> block in
/// <c>project.godot</c>.
/// </summary>
/// <remarks>
/// Values are the bit masks Godot assigned, so the layer and mask numbers in the entity
/// scenes can be read straight across: ships 1, asteroids 2, projectiles 4, pickups 8,
/// walls 16.
/// </remarks>
[Flags]
public enum CollisionLayer
{
    None = 0,
    Ships = 1,
    Asteroids = 2,
    Projectiles = 4,
    Pickups = 8,
    Walls = 16,
}
