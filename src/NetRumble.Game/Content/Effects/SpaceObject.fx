// Consistent lighting for the world's solid bodies (asteroids, power-ups, mines).
//
// The sample's art is flat and unlit: every rock is drawn with the same baked
// highlight, so a field of asteroids reads as a collection of stickers rather than
// as lit objects, and a spinning rock carries its highlight around with it.
//
// Rather than author normal maps for every texture, this fakes a normal per fragment
// by treating the sprite quad as a hemisphere: UV distance from the centre gives the
// x/y of the normal, and z falls out of the unit-sphere identity. That is a very good
// match for asteroids and pickups, which are all roughly ball-shaped.
//
// The crucial part is that the normal is rotated into *world* space before it is lit.
// Asteroids spin, and a normal left in local space would carry its highlight around
// with the rock. Rotating it nails the highlight to one place in the world no matter
// how the body tumbles - which is what makes every object look lit by one source.
//
// Godot's version read that rotation out of MODEL_MATRIX in the vertex stage. There is
// no equivalent here: SpriteBatch bakes each sprite's rotation into the quad corners
// it emits and its vertex format has no spare channel to carry the angle in, and a
// single effect parameter cannot vary per sprite inside one batch. So the basis is
// instead recovered from the screen-space derivatives of the texture coordinate, which
// encode exactly the same rotation - see BodyNormal.

// All ten uniforms below are set from SpaceObjectLighting.Apply. They are left
// uninitialised deliberately: the compiler discards initialisers on external globals,
// so a default written here would be silently ignored and read back as zero.
float2 LightDirection;
float LightElevation;
float Ambient;
float3 ShadowTint;
float Sphericity;
float SpecularStrength;
float SpecularPower;
float RimStrength;
float RimPower;
float3 RimColor;

// SpriteBatch looks for a parameter of this exact name and fills it in with the
// projection it would otherwise have used itself.
float4x4 MatrixTransform;

Texture2D SpriteTexture : register(t0);

SamplerState SpriteTextureSampler : register(s0);

struct VertexInput
{
    float4 Position : POSITION0;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

PixelInput SpriteVertexShader(VertexInput input)
{
    PixelInput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

/// The fake hemisphere normal, rotated into world space.
///
/// The rotation comes from the derivatives of the texture coordinate. ddx/ddy give the
/// 2x2 Jacobian mapping a step in screen space to a step in UV space; its inverse maps
/// a step in the sprite's own space back out to screen space, which is precisely the
/// sprite's rotation (times its scale, which normalising removes). Screen Y and world Y
/// both point down, so no handedness fixup is needed.
float3 BodyNormal(float2 uv)
{
    float2 offset = clamp((uv - 0.5) * 2.0, -1.0, 1.0);
    float radiusSquared = min(dot(offset, offset), 1.0);
    float depth = sqrt(1.0 - radiusSquared);
    float3 localNormal = normalize(float3(offset * Sphericity, max(depth, 0.001)));

    float2 duvdx = ddx(uv);
    float2 duvdy = ddy(uv);
    float det = (duvdx.x * duvdy.y) - (duvdy.x * duvdx.y);

    // A degenerate quad (a zero-area or fully clipped sprite) leaves the basis
    // unrecoverable; falling back to the identity shades it as an unrotated body
    // rather than producing NaNs.
    float2 basisX = float2(1.0, 0.0);
    float2 basisY = float2(0.0, 1.0);

    if (abs(det) > 1e-12)
    {
        float invDet = 1.0 / det;
        basisX = normalize(float2(duvdy.y, -duvdx.y) * invDet);
        basisY = normalize(float2(-duvdy.x, duvdx.x) * invDet);
    }

    return float3((localNormal.x * basisX) + (localNormal.y * basisY), localNormal.z);
}

float4 SpritePixelShader(PixelInput input) : SV_Target
{
    // Texture times modulate, matching Godot's COLOR, so an entity's own tinting is
    // already folded in. The content pipeline premultiplies alpha, and every term
    // below is either a scale of the premultiplied colour or is multiplied by alpha
    // itself, so the result stays premultiplied.
    float4 base = SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;

    float3 worldNormal = BodyNormal(input.TexCoord);

    float3 toLight = normalize(float3(-LightDirection, max(LightElevation, 0.001)));
    float lambert = max(dot(worldNormal, toLight), 0.0);

    // Half-vector specular against a camera looking straight down +Z.
    float3 halfVector = normalize(toLight + float3(0.0, 0.0, 1.0));
    float specular = pow(max(dot(worldNormal, halfVector), 0.0), SpecularPower) * SpecularStrength;

    // Rim only on the lit hemisphere, otherwise it outlines the shadow side too.
    float rim = pow(1.0 - worldNormal.z, RimPower) * lambert * RimStrength;

    float exposure = Ambient + ((1.0 - Ambient) * lambert);
    float3 shaded = lerp(base.rgb * ShadowTint, base.rgb, lambert) * exposure;
    shaded += (specular + rim) * RimColor * base.a;

    return float4(shaded, base.a);
}

technique SpaceObject
{
    pass P0
    {
        VertexShader = compile vs_6_0 SpriteVertexShader();
        PixelShader = compile ps_6_0 SpritePixelShader();
    }
}
