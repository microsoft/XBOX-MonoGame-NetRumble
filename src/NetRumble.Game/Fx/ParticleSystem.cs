using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Core;
using NetRumble.Game.Content;

namespace NetRumble.Game.Fx;

/// <summary>
/// A fixed-capacity CPU particle pool, replacing the <c>CPUParticles2D</c> nodes that
/// <c>scripts/fx/particle_manager.gd</c> instantiated per effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>This reimplements Godot's <c>CPUParticles2D</c> integrator, not a generic one.</b>
/// The per-step order in <c>cpu_particles_2d.cpp</c> is: apply gravity to velocity, then
/// damp, then advance position. Damping is a linear decay of <em>speed</em> - the particle
/// loses <c>damping * delta</c> pixels per second of speed every step and stops dead at
/// zero - rather than the exponential drag that "damping" usually implies. Reordering
/// these or substituting drag changes how far every burst throws, so the sequence in
/// <see cref="Update"/> is load-bearing.
/// </para>
/// <para>
/// <b>Pooling instead of node churn.</b> The Godot original allocated a scene per event and
/// freed it on completion, which was acceptable there because the engine amortised it.
/// Here a flat array is both simpler and avoids a garbage spike during a firefight. A burst
/// that will not fit is dropped rather than growing the pool, matching how Godot silently
/// caps a node at its <c>amount</c>.
/// </para>
/// <para>
/// <b>Draw order.</b> Additive particles are drawn in one batch and alpha-blended ones in a
/// second, always additive first. That reproduces the one case that matters - the smoke
/// layer of <c>ship_explosion.tscn</c> is a child node, so Godot drew it after its parent's
/// sparks. Ordering <em>between</em> concurrent bursts is not preserved, which is harmless
/// because additive blending is order-independent and the only alpha-blended emitter in the
/// game is that smoke.
/// </para>
/// </remarks>
public sealed class ParticleSystem
{
    /// <summary>
    /// Pool size. The largest single burst is <c>ship_explosion</c> at 68 particles, so this
    /// leaves room for roughly thirty simultaneous explosions.
    /// </summary>
    private const int Capacity = 2048;

    /// <summary>
    /// Blend state for the effects whose Godot <c>CanvasItemMaterial</c> set
    /// <c>blend_mode = 1</c> (add).
    /// </summary>
    /// <remarks>
    /// Not <see cref="BlendState.Additive"/>. That one is <c>(SourceAlpha, One)</c>, which
    /// assumes a straight-alpha texture. The content pipeline premultiplies, and the tint
    /// below folds alpha into RGB as well, so alpha is already baked into the colour and
    /// applying it a second time would darken every particle by its own fade factor
    /// squared. <c>(One, One)</c> is the premultiplied form of add.
    /// </remarks>
    private static readonly BlendState PremultipliedAdditive = new()
    {
        Name = "PremultipliedAdditive",
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    private readonly AssetRegistry _assets;
    private readonly Random _random;
    private readonly Particle[] _particles = new Particle[Capacity];

    /// <summary>
    /// One past the highest slot ever used. Keeps the update and draw loops off the tail of
    /// the pool during the early game instead of always walking all of <see cref="Capacity"/>.
    /// </summary>
    private int _highWater;

    public ParticleSystem(AssetRegistry assets, Random random)
    {
        _assets = assets;
        _random = random;
    }

    /// <summary>Live particle count, for the debug overlay.</summary>
    public int ActiveCount { get; private set; }

    /// <summary>
    /// Fires the effect mapped to <paramref name="eventType"/>, if it has one.
    /// </summary>
    /// <param name="tint">
    /// Godot's <c>modulate</c> on the spawned effect: multiplied into every emitter's own
    /// colour, including nested ones. <see cref="Color.White"/> leaves the authored colours
    /// alone.
    /// </param>
    public void PlayEvent(GameplayEventType eventType, Vector2 position, Color tint)
    {
        if (EffectLibrary.Effects.TryGetValue(eventType, out var burst))
        {
            Spawn(burst, position, tint, 1.0f);
        }
    }

    /// <summary>
    /// A generic explosion built from the ship-destruction effect, matching
    /// <c>ParticleManager.explosion</c>.
    /// </summary>
    /// <param name="scale">
    /// Godot set the effect node's <c>scale</c>, which scales the particles' offsets from the
    /// origin as well as their sprites. Since offsets are integrated from velocity, that is
    /// equivalent to scaling velocity, damping and sprite scale together - see
    /// <see cref="Spawn"/>.
    /// </param>
    public void Explosion(Vector2 position, float scale, Color tint)
        => Spawn(EffectLibrary.ShipExplosion, position, tint, scale);

    /// <summary>Releases every emitter of a burst at once (<c>explosiveness = 1.0</c>).</summary>
    public void Spawn(ParticleBurstDefinition burst, Vector2 position, Color tint, float scale)
    {
        foreach (var emitter in burst.Emitters)
        {
            var texture = _assets.Texture(emitter.TextureKey);
            var colour = Modulate(emitter.Color, tint);
            var baseAngle = MathF.Atan2(emitter.Direction.Y, emitter.Direction.X);
            var spread = MathHelper.ToRadians(emitter.SpreadDegrees);

            for (var i = 0; i < emitter.Amount; i++)
            {
                var slot = FindFreeSlot();
                if (slot < 0)
                {
                    return;
                }

                // Godot: angle = atan2(direction) + deg_to_rad(randf_range(-1, 1) * spread).
                var angle = baseAngle + (NextFloat(-1f, 1f) * spread);
                var speed = MathHelper.Lerp(
                    emitter.InitialVelocityMin,
                    emitter.InitialVelocityMax,
                    NextFloat(0f, 1f));

                _particles[slot] = new Particle
                {
                    Alive = true,
                    Texture = texture,
                    Origin = new Vector2(texture.Width / 2f, texture.Height / 2f),
                    Position = position,
                    Velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed * scale,
                    Gravity = emitter.Gravity * scale,
                    Damping = emitter.Damping * scale,
                    Age = 0f,
                    Lifetime = emitter.Lifetime,
                    Scale = MathHelper.Lerp(emitter.ScaleMin, emitter.ScaleMax, NextFloat(0f, 1f)) * scale,
                    Colour = colour,
                    Additive = emitter.Additive,
                };

                if (slot >= _highWater)
                {
                    _highWater = slot + 1;
                }
            }
        }
    }

    /// <summary>Drops every live particle, matching <c>ParticleManager.clear_all</c>.</summary>
    public void Clear()
    {
        Array.Clear(_particles, 0, _highWater);
        _highWater = 0;
        ActiveCount = 0;
    }

    /// <summary>Advances the pool by one frame, following Godot's integration order.</summary>
    public void Update(float delta)
    {
        var active = 0;
        var newHighWater = 0;

        for (var i = 0; i < _highWater; i++)
        {
            ref var p = ref _particles[i];

            if (!p.Alive)
            {
                continue;
            }

            p.Age += delta;

            if (p.Age >= p.Lifetime)
            {
                p.Alive = false;
                p.Texture = null;
                continue;
            }

            p.Velocity += p.Gravity * delta;

            if (p.Damping > 0f)
            {
                var speed = p.Velocity.Length() - (p.Damping * delta);

                // Godot stops the particle outright rather than letting damping reverse it.
                p.Velocity = speed <= 0f ? Vector2.Zero : Vector2.Normalize(p.Velocity) * speed;
            }

            p.Position += p.Velocity * delta;

            active++;
            newHighWater = i + 1;
        }

        _highWater = newHighWater;
        ActiveCount = active;
    }

    /// <summary>
    /// Draws the pool through the gameplay view transform. The particle manager was parented
    /// to the world container after the world itself, so effects sit above every entity.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Matrix view)
    {
        if (ActiveCount == 0)
        {
            return;
        }

        DrawPass(spriteBatch, view, PremultipliedAdditive, additive: true);
        DrawPass(spriteBatch, view, BlendState.AlphaBlend, additive: false);
    }

    private void DrawPass(SpriteBatch spriteBatch, Matrix view, BlendState blend, bool additive)
    {
        var opened = false;

        for (var i = 0; i < _highWater; i++)
        {
            ref var p = ref _particles[i];

            if (!p.Alive || p.Additive != additive || p.Texture is null)
            {
                continue;
            }

            if (!opened)
            {
                spriteBatch.Begin(
                    blendState: blend,
                    samplerState: SamplerState.LinearClamp,
                    transformMatrix: view);
                opened = true;
            }

            // The shared two-stop colour ramp: white at alpha 1 fading linearly to alpha 0
            // over the particle's life. Scaling the whole Color - RGB included - is what
            // keeps it premultiplied.
            var fade = 1f - (p.Age / p.Lifetime);

            spriteBatch.Draw(
                p.Texture,
                p.Position,
                null,
                p.Colour * fade,
                0f,
                p.Origin,
                p.Scale,
                SpriteEffects.None,
                0f);
        }

        if (opened)
        {
            spriteBatch.End();
        }
    }

    /// <summary>
    /// Godot's <c>modulate</c>: a componentwise multiply of two straight colours.
    /// </summary>
    private static Color Modulate(Color a, Color b) => new(
        a.R * b.R / 255,
        a.G * b.G / 255,
        a.B * b.B / 255,
        a.A * b.A / 255);

    private int FindFreeSlot()
    {
        for (var i = 0; i < Capacity; i++)
        {
            if (!_particles[i].Alive)
            {
                return i;
            }
        }

        return -1;
    }

    private float NextFloat(float min, float max) => min + ((float)_random.NextDouble() * (max - min));

    private struct Particle
    {
        public bool Alive;
        public Texture2D? Texture;
        public Vector2 Origin;
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector2 Gravity;
        public float Damping;
        public float Age;
        public float Lifetime;
        public float Scale;
        public Color Colour;
        public bool Additive;
    }
}
