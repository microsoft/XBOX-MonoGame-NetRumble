using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// The tuning for <c>Content/Effects/SpaceObject.fx</c>, ported from the uniform
/// defaults on <c>assets/shaders/space_object.gdshader</c> and the single shared
/// <c>space_object_lighting.tres</c> every entity scene pointed at.
/// </summary>
/// <remarks>
/// <para>
/// The values live here rather than as initialisers in the .fx because the shader
/// compiler discards initialisers on external globals ("Initializer of external global
/// will be ignored") - an unset parameter would simply read as zero, which is a black
/// light from nowhere.
/// </para>
/// <para>
/// <b>Godot's dynamic-light pass is not ported.</b> The source shader also had a
/// <c>light()</c> pass that let a passing weapon bolt, explosion or pickup flare the
/// body it flew past. That pass is driven by Godot's Light2D system, of which MonoGame
/// has no equivalent; reproducing it would mean building a 2D light rig for this one
/// effect. The base pass below is the part that carries the look, so only it is ported.
/// </para>
/// </remarks>
internal static class SpaceObjectLighting
{
    public const string ContentPath = "Effects/SpaceObject";

    /// <summary>
    /// Direction the light <i>travels</i>, in world space: a sun above and to the left,
    /// so lit edges face up-left and shadows fall down-right.
    /// </summary>
    private static readonly Vector2 LightDirection = new(0.55f, 0.83f);

    /// <summary>
    /// How much of the light comes straight out of the screen. 0 is fully edge-on (a
    /// hard terminator down the middle of every body), 1 is straight down the camera.
    /// </summary>
    private const float LightElevation = 0.45f;

    /// <summary>Floor brightness on the unlit side. Pure black reads as a hole in space.</summary>
    private const float Ambient = 0.28f;

    /// <summary>Colour the shadow side tints towards, keeping it cool rather than merely dark.</summary>
    private static readonly Vector3 ShadowTint = new(0.36f, 0.42f, 0.62f);

    /// <summary>How spherical the fake normal is. 1.0 is a full hemisphere.</summary>
    private const float Sphericity = 1.0f;

    private const float SpecularStrength = 0.35f;
    private const float SpecularPower = 12.0f;

    /// <summary>
    /// A thin lit fringe along the silhouette on the lit side, which is what sells a
    /// sprite as a round body rather than a shaded disc.
    /// </summary>
    private const float RimStrength = 0.45f;

    private const float RimPower = 3.0f;
    private static readonly Vector3 RimColor = new(1.0f, 0.96f, 0.88f);

    public static void Apply(Effect effect)
    {
        var parameters = effect.Parameters;
        parameters["LightDirection"].SetValue(LightDirection);
        parameters["LightElevation"].SetValue(LightElevation);
        parameters["Ambient"].SetValue(Ambient);
        parameters["ShadowTint"].SetValue(ShadowTint);
        parameters["Sphericity"].SetValue(Sphericity);
        parameters["SpecularStrength"].SetValue(SpecularStrength);
        parameters["SpecularPower"].SetValue(SpecularPower);
        parameters["RimStrength"].SetValue(RimStrength);
        parameters["RimPower"].SetValue(RimPower);
        parameters["RimColor"].SetValue(RimColor);
    }

    /// <summary>
    /// Supplies the projection <see cref="SpriteBatch"/> would otherwise have supplied.
    /// </summary>
    /// <remarks>
    /// SpriteBatch only fills <c>MatrixTransform</c> in on its own internal effect, and
    /// this one brings a vertex shader that displaces it. Reproduced from
    /// <c>SpriteEffect</c>: a y-down orthographic projection over the viewport, with the
    /// batch's transform in front of it. Without this every vertex is multiplied by a
    /// zero matrix and the pass silently draws nothing at all.
    /// </remarks>
    public static void SetTransform(Effect effect, Viewport viewport, Matrix transform)
    {
        var projection = Matrix.CreateOrthographicOffCenter(
            0.0f,
            Math.Max(viewport.Width, 1),
            Math.Max(viewport.Height, 1),
            0.0f,
            0.0f,
            1.0f);

        effect.Parameters["MatrixTransform"].SetValue(transform * projection);
    }
}
