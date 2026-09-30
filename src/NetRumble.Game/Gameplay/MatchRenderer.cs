using NetRumble.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Core;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;
using NetRumble.Game.Content;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// Draws a <see cref="World"/>. Replaces the <c>Sprite2D</c> hierarchies that
/// <c>scenes/gameplay/entities/*.tscn</c> attached to every body.
/// </summary>
/// <remarks>
/// <para>
/// Every sprite scale and offset here is lifted from those scene files, because the
/// simulation deliberately publishes no render data beyond what it derives from its own
/// state - see the "Render state" region on <see cref="Ship"/>.
/// </para>
/// <para>
/// <b>Draw order replaces z_index.</b> Godot sorted by <c>z_index</c>: barrier -10,
/// thruster -1, base 0, overlay 1, shield 2. <see cref="SpriteBatch"/> in
/// <see cref="SpriteSortMode.Deferred"/> draws in call order, so the passes below are
/// sequenced to match and must not be reordered.
/// </para>
/// </remarks>
public sealed class MatchRenderer
{
    /// <summary>Sprite scales baked into <c>ship.tscn</c>.</summary>
    private const float ShipBodyScale = 0.75f;

    private const float ShipThrusterScale = 0.26666668f;
    private const float ShipShieldScale = 0.2962963f;

    /// <summary>Alpha the thruster is drawn at, from <c>ship.gd</c>.</summary>
    private const float ThrusterAlpha = 0.75f;

    /// <summary>
    /// The invulnerability tween in <c>ship.gd</c>: six 0.2 s segments through the colour
    /// wheel and back to red.
    /// </summary>
    private const float InvulnerabilitySegment = 0.2f;

    private static readonly Color[] InvulnerabilityColours =
    [
        Color.Red,
        Color.Yellow,
        Color.Lime,
        Color.Cyan,
        Color.Blue,
        Color.Magenta,
        Color.Red,
    ];

    private readonly AssetRegistry _assets;
    private readonly UiTheme _theme;

    /// <summary>
    /// The shared lighting effect for the world's solid bodies. Loaded once and
    /// configured once: every uniform is constant for the life of the match, so there
    /// is exactly one light in the world and moving it would move every shadow.
    /// </summary>
    /// <remarks>
    /// Null when the compiled shader does not match the running backend - which is the
    /// case for a console build whose content was built for desktop, because shader
    /// bytecode is the one asset kind that is not portable. The pass then draws unlit
    /// rather than not at all; see <see cref="AssetRegistry.TryEffect"/>.
    /// </remarks>
    private readonly Effect? _lighting;

    /// <summary>Random initial cap rotations, as <c>barrier.gd</c> rolls at layout time.</summary>
    private readonly float[] _capPhases;

    private float _barrierSpin;

    public MatchRenderer(AssetRegistry assets, UiTheme theme, Random random)
    {
        _assets = assets;
        _theme = theme;
        _lighting = assets.TryEffect(SpaceObjectLighting.ContentPath);

        if (_lighting is not null)
        {
            SpaceObjectLighting.Apply(_lighting);
        }

        _capPhases =
        [
            (float)(random.NextDouble() * MathHelper.TwoPi),
            (float)(random.NextDouble() * MathHelper.TwoPi),
            (float)(random.NextDouble() * MathHelper.TwoPi),
            (float)(random.NextDouble() * MathHelper.TwoPi),
        ];
    }

    /// <summary>Advances purely decorative animation that no simulation state drives.</summary>
    public void Update(float delta) => _barrierSpin += NRConst.BarrierRotationSpeed * delta;

    /// <summary>
    /// Draws the whole world. Opens and closes its own batches, because the tiled barrier
    /// needs a wrapping sampler, the sprites need a clamping one, and the solid bodies
    /// need the lighting effect.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, World world, Matrix view)
    {
        // Pass 1: barrier walls. LinearWrap makes the oversized source rectangle tile the
        // texture, which is how barrier.gd stretched its region_rect.
        spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearWrap,
            transformMatrix: view);

        DrawBarrierWalls(spriteBatch, world);

        spriteBatch.End();

        spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearClamp,
            transformMatrix: view);

        DrawBarrierCaps(spriteBatch, world);

        // Thrusters carry z_index -1 in ship.tscn, so in Godot they drew beneath every
        // field object rather than beneath their own ship only. Hoisting them into their
        // own pass ahead of the field reproduces that global ordering.
        foreach (var ship in world.Ships.Values)
        {
            if (ship.IsActive)
            {
                DrawShipThruster(spriteBatch, ship);
            }
        }

        spriteBatch.End();

        // Pass 3: the solid bodies, lit. Godot gave these a shared ShaderMaterial, which
        // is per-node and so cost it no batching; a SpriteBatch effect is per-batch, so
        // they have to be pulled out of the object walk into a pass of their own. That
        // hoists asteroids, power-ups and mines above the other projectiles instead of
        // interleaving them by spawn order. Every one of these is a small, mostly
        // non-overlapping sprite, so the reordering is not visible in practice.
        //
        // The effect supplies its own vertex shader, which replaces SpriteBatch's. That
        // also replaces the only thing that fills in MatrixTransform, so the projection
        // has to be handed over here or every quad collapses and the pass draws nothing.
        // With no usable effect the pass runs as an ordinary SpriteBatch batch, which
        // loses the highlight and keeps the game.
        if (_lighting is not null)
        {
            SpaceObjectLighting.SetTransform(_lighting, spriteBatch.GraphicsDevice.Viewport, view);
        }

        spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearClamp,
            effect: _lighting,
            transformMatrix: view);

        foreach (var obj in world.GameObjects.Values)
        {
            if (!obj.IsActive)
            {
                continue;
            }

            switch (obj)
            {
                case Asteroid asteroid:
                    DrawAsteroid(spriteBatch, asteroid);
                    break;
                case PowerUp powerUp:
                    DrawPowerUp(spriteBatch, powerUp);
                    break;
                case MineProjectile mine:
                    DrawProjectile(spriteBatch, world, mine);
                    break;
            }
        }

        spriteBatch.End();

        spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearClamp,
            transformMatrix: view);

        foreach (var obj in world.GameObjects.Values)
        {
            if (obj is Projectile and not MineProjectile && obj.IsActive)
            {
                DrawProjectile(spriteBatch, world, (Projectile)obj);
            }
        }

        // Ship hulls last: asteroids are added to the tree before ships, so at the same
        // z_index of 0 the ships won the tie and drew above the field.
        foreach (var ship in world.Ships.Values)
        {
            if (ship.IsActive)
            {
                DrawShipBody(spriteBatch, ship);
            }
        }

        spriteBatch.End();
    }

    private void DrawBarrierWalls(SpriteBatch spriteBatch, World world)
    {
        var horizontal = _assets.Texture("Barrier_Horizontal");
        var vertical = _assets.Texture("Barrier_Vertical");

        var width = world.WorldWidth;
        var height = world.WorldHeight;
        var scale = NRConst.BarrierEndScale;

        // Region length is divided by the sprite scale, exactly as barrier.gd does, so the
        // scaled sprite spans the wall.
        var spanX = (int)MathF.Round(width / scale);
        var spanY = (int)MathF.Round(height / scale);

        DrawTiled(spriteBatch, horizontal, new Vector2(width / 2f, 0f), spanX, horizontal.Height, scale);
        DrawTiled(spriteBatch, horizontal, new Vector2(width / 2f, height), spanX, horizontal.Height, scale);
        DrawTiled(spriteBatch, vertical, new Vector2(0f, height / 2f), vertical.Width, spanY, scale);
        DrawTiled(spriteBatch, vertical, new Vector2(width, height / 2f), vertical.Width, spanY, scale);
    }

    private static void DrawTiled(
        SpriteBatch spriteBatch,
        Texture2D texture,
        Vector2 centre,
        int sourceWidth,
        int sourceHeight,
        float scale) =>
        spriteBatch.Draw(
            texture,
            centre,
            new Rectangle(0, 0, sourceWidth, sourceHeight),
            Color.White,
            0f,
            new Vector2(sourceWidth / 2f, sourceHeight / 2f),
            scale,
            SpriteEffects.None,
            0f);

    private void DrawBarrierCaps(SpriteBatch spriteBatch, World world)
    {
        var cap = _assets.Texture("Barrier_End");
        var origin = new Vector2(cap.Width / 2f, cap.Height / 2f);

        Span<Vector2> corners =
        [
            new(0f, 0f),
            new(world.WorldWidth, 0f),
            new(0f, world.WorldHeight),
            new(world.WorldWidth, world.WorldHeight),
        ];

        for (var i = 0; i < corners.Length; i++)
        {
            spriteBatch.Draw(
                cap,
                corners[i],
                null,
                Color.White,
                _capPhases[i] + _barrierSpin,
                origin,
                NRConst.BarrierEndScale,
                SpriteEffects.None,
                0f);
        }
    }

    private void DrawAsteroid(SpriteBatch spriteBatch, Asteroid asteroid)
    {
        var texture = _assets.Texture(asteroid.Tuning.TextureFor(asteroid.Variation));

        // The painted rock is lined up with the collision radius, matching
        // asteroid.gd's `radius / texture_radius`.
        var scale = asteroid.Radius / asteroid.Tuning.TextureRadius;

        spriteBatch.Draw(
            texture,
            ToXna(asteroid.Position),
            null,
            Color.White,
            asteroid.Rotation,
            new Vector2(texture.Width / 2f, texture.Height / 2f),
            scale,
            SpriteEffects.None,
            0f);
    }

    /// <summary>
    /// Draws a pickup as a tinted silhouette with a short label.
    /// </summary>
    /// <remarks>
    /// There are three power-up textures and thirty-two pickups, so the texture only picks
    /// the silhouette; the tint and the two- or three-letter label carry the actual
    /// identity. That is the Godot build's arrangement and the reason for it holds here
    /// too - a label is far cheaper than commissioning twenty-nine more sprites, and stays
    /// legible at the zoom the game is played at.
    /// </remarks>
    private void DrawPowerUp(SpriteBatch spriteBatch, PowerUp powerUp)
    {
        var definition = powerUp.Definition;
        var texture = _assets.Texture(definition.Texture);
        var origin = new Vector2(texture.Width / 2f, texture.Height / 2f);
        var position = ToXna(powerUp.Position);

        spriteBatch.Draw(
            texture,
            position,
            null,
            ToXna(definition.Tint),
            powerUp.Rotation,
            origin,
            powerUp.PulseScale,
            SpriteEffects.None,
            0f);

        if (definition.Label.Length == 0)
        {
            return;
        }

        var font = _theme.Small;
        var size = font.MeasureString(definition.Label);

        // Drawn unrotated over a sprite that spins, because a label that tumbles with the
        // pickup is unreadable for most of its cycle - which defeats the entire point of
        // labelling it.
        spriteBatch.DrawString(
            font,
            definition.Label,
            position,
            new Color(255, 255, 255, 230),
            0f,
            size * 0.5f,
            1.0f,
            SpriteEffects.None,
            0f);
    }

    private void DrawProjectile(SpriteBatch spriteBatch, World world, Projectile projectile)
    {
        var texture = _assets.Texture(projectile.Tuning.Texture);

        // Every projectile is tinted with the colour of the ship that fired it, so players
        // can tell whose shots are whose. A shot outlives its owner, so fall back to white
        // rather than skipping the draw.
        var owner = world.GetShipById(projectile.OwnerId);
        var tint = owner is null ? Color.White : ToXna(owner.ShipColor);

        // The weapon's own colour is then multiplied over the owner's, so a plasma bolt
        // and a railgun slug from the same ship still read as different weapons while both
        // remaining recognisably that player's. A fully transparent shot colour means the
        // weapon specifies none, which is the laser-derived default.
        var spec = projectile.Spec;

        if (spec.ShotColor.A > 0)
        {
            tint = new Color(
                (byte)(tint.R * spec.ShotColor.R / 255),
                (byte)(tint.G * spec.ShotColor.G / 255),
                (byte)(tint.B * spec.ShotColor.B / 255),
                tint.A);
        }

        spriteBatch.Draw(
            texture,
            ToXna(projectile.Position),
            null,
            tint,
            projectile.Rotation + projectile.Tuning.SpriteRotation,
            new Vector2(texture.Width / 2f, texture.Height / 2f),
            spec.SpriteScale,
            SpriteEffects.None,
            0f);
    }

    private void DrawShipThruster(SpriteBatch spriteBatch, Ship ship)
    {
        if (!ship.IsThrusting)
        {
            return;
        }

        var thruster = _assets.Texture("ShipShield_Move");

        // The thruster sprite is authored pointing forward and rotated by pi in the
        // scene so it trails the ship.
        spriteBatch.Draw(
            thruster,
            ToXna(ship.Position),
            null,
            ToXna(ship.ShipColor) * ThrusterAlpha,
            ship.Rotation + MathHelper.Pi,
            new Vector2(thruster.Width / 2f, thruster.Height / 2f),
            ShipThrusterScale,
            SpriteEffects.None,
            0f);
    }

    private void DrawShipBody(SpriteBatch spriteBatch, Ship ship)
    {
        var position = ToXna(ship.Position);
        var shipColor = ToXna(ship.ShipColor);

        // A cloaked ship is faded rather than hidden. Drawing nothing at all would be a
        // stronger effect, but it also removes the only cue the cloaked player has that
        // their own ship still exists, and leaves an opponent nothing to react to at close
        // range - which reads as a bug rather than as an ability.
        if (ship.HasBuff(BuffType.Cloak))
        {
            shipColor *= Ship.CloakAlpha;
        }

        var baseTexture = _assets.ShipTexture(ship.ShipStyleId, "Base");
        spriteBatch.Draw(
            baseTexture,
            position,
            null,
            shipColor,
            ship.Rotation,
            new Vector2(baseTexture.Width / 2f, baseTexture.Height / 2f),
            ShipBodyScale,
            SpriteEffects.None,
            0f);

        // The overlay is the untinted detail layer; it is what keeps a dark hull readable
        // against the starfield.
        var overlay = _assets.ShipTexture(ship.ShipStyleId, "Overlay");
        spriteBatch.Draw(
            overlay,
            position,
            null,
            ship.HasBuff(BuffType.Cloak) ? Color.White * Ship.CloakAlpha : Color.White,
            ship.Rotation,
            new Vector2(overlay.Width / 2f, overlay.Height / 2f),
            ShipBodyScale,
            SpriteEffects.None,
            0f);

        if (ship.ShieldAlpha <= 0.0f)
        {
            return;
        }

        var shield = _assets.Texture("ShipShield_Base");

        // Godot multiplied modulate (white, shield alpha) by self_modulate (the ship
        // colour, or the invulnerability cycle while spawn protection is up).
        var shieldTint = ship.InvulnerabilityPhase is { } phase
            ? InvulnerabilityColour(phase)
            : shipColor;

        spriteBatch.Draw(
            shield,
            position,
            null,
            shieldTint * ship.ShieldAlpha,
            ship.Rotation,
            new Vector2(shield.Width / 2f, shield.Height / 2f),
            ShipShieldScale,
            SpriteEffects.None,
            0f);
    }

    /// <summary>
    /// Samples the looping invulnerability colour cycle. The tween interpolated between
    /// consecutive key colours, so this lerps rather than stepping.
    /// </summary>
    private static Color InvulnerabilityColour(float phase)
    {
        var segments = InvulnerabilityColours.Length - 1;
        var loop = segments * InvulnerabilitySegment;

        var t = phase % loop;
        if (t < 0.0f)
        {
            t += loop;
        }

        var index = Math.Min((int)(t / InvulnerabilitySegment), segments - 1);
        var local = (t - (index * InvulnerabilitySegment)) / InvulnerabilitySegment;

        return Color.Lerp(InvulnerabilityColours[index], InvulnerabilityColours[index + 1], local);
    }

    private static Vector2 ToXna(System.Numerics.Vector2 value) => new(value.X, value.Y);

    private static Color ToXna(RgbaColor value) => new(value.R, value.G, value.B, value.A);
}
