using System.Numerics;

namespace NetRumble.Core.Objects;

/// <summary>
/// The replicated state of one entity, replacing the <c>Dictionary</c> that
/// <c>game_object.gd::serialize()</c> returned.
/// </summary>
/// <remarks>
/// A struct rather than a dictionary because these are produced for every object in the
/// world 30 times a second. The GDScript version allocated a dictionary and eleven boxed
/// values per object per snapshot, which the netcode then had to re-read by string key.
/// </remarks>
public struct GameObjectSnapshot
{
    public GameObjectType Type;
    public int Id;
    public Vector2 Position;
    public Vector2 Velocity;
    public float Mass;
    public float Radius;
    public float Rotation;
    public float Health;
    public bool IsActive;
}
