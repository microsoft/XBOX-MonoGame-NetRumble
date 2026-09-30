using Microsoft.Xna.Framework;

namespace NetRumble.Game.Fx;

/// <summary>
/// One <c>CPUParticles2D</c> node's worth of emission settings, lifted field-for-field
/// from the <c>.tscn</c> files under <c>scenes/gameplay/fx/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Godot port kept these numbers in scene files so they stayed editable in the
/// inspector. MonoGame has no scene format, so they move into code - but the field names
/// below are deliberately kept identical to Godot's property names so the two can still
/// be diffed by eye against the original <c>.tscn</c>.
/// </para>
/// <para>
/// Every shipped effect sets <c>one_shot = true</c> and <c>explosiveness = 1.0</c>, which
/// in Godot means "emit the whole <c>amount</c> on the first frame and then stop". That is
/// the only emission mode <see cref="ParticleSystem"/> implements, and it is why these are
/// called bursts rather than emitters with a rate.
/// </para>
/// </remarks>
/// <param name="TextureKey"><see cref="Content.AssetRegistry"/> key for the sprite.</param>
/// <param name="Amount">Godot <c>amount</c>: particles released by the burst.</param>
/// <param name="Lifetime">Godot <c>lifetime</c>, in seconds.</param>
/// <param name="Direction">Godot <c>direction</c>: the centre of the emission cone.</param>
/// <param name="SpreadDegrees">
/// Godot <c>spread</c>: the half-angle of the cone, in degrees. Every shipped effect uses
/// 180, which is a full circle.
/// </param>
/// <param name="Gravity">Godot <c>gravity</c>, in pixels per second squared.</param>
/// <param name="InitialVelocityMin">Godot <c>initial_velocity_min</c>.</param>
/// <param name="InitialVelocityMax">Godot <c>initial_velocity_max</c>.</param>
/// <param name="Damping">
/// Godot <c>damping_min</c>/<c>damping_max</c>, which every shipped effect sets to the
/// same value, so it collapses to a scalar. This is linear <em>speed</em> decay in pixels
/// per second squared, not a drag coefficient - see <see cref="ParticleSystem"/>.
/// </param>
/// <param name="ScaleMin">Godot <c>scale_amount_min</c>.</param>
/// <param name="ScaleMax">Godot <c>scale_amount_max</c>.</param>
/// <param name="Color">
/// Godot <c>color</c>, as a straight (non-premultiplied) tint. The <c>color_ramp</c> is not
/// a field here because all seven effects use the same two-stop white ramp fading alpha
/// 1 to 0, which <see cref="ParticleSystem"/> applies unconditionally.
/// </param>
/// <param name="Additive">
/// True when the node carried a <c>CanvasItemMaterial</c> with <c>blend_mode = 1</c>.
/// </param>
public sealed record ParticleEmitterDefinition(
    string TextureKey,
    int Amount,
    float Lifetime,
    Vector2 Direction,
    float SpreadDegrees,
    Vector2 Gravity,
    float InitialVelocityMin,
    float InitialVelocityMax,
    float Damping,
    float ScaleMin,
    float ScaleMax,
    Color Color,
    bool Additive);

/// <summary>
/// A whole effect scene: the root emitter plus any nested ones.
/// </summary>
/// <remarks>
/// Only <c>ship_explosion.tscn</c> nests a second emitter (its smoke layer). The nesting is
/// preserved rather than flattened into two independent effects because Godot's
/// <c>modulate</c> propagates from a parent <c>CanvasItem</c> to its children, so the event
/// tint has to reach both, and because <c>one_shot_effect.gd</c> frees the whole node only
/// once every nested emitter has finished.
/// </remarks>
public sealed record ParticleBurstDefinition(string Name, ParticleEmitterDefinition[] Emitters);
