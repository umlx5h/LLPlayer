namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> VS => @"
cbuffer cBuf : register(b0)
{
    matrix mat;
    float4 cropRegion;
}

struct VSInput
{
    float4 Position : POSITION;
    float2 Texture  : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture  : TEXCOORD0;
};

PSInput main(VSInput vsi)
{
    PSInput psi;

    psi.Position= mul(vsi.Position, mat);
    psi.Texture = lerp(cropRegion.xy, cropRegion.zw, vsi.Texture);

    return psi;
}
"u8;

    static ReadOnlySpan<byte> VSSimple => @"
struct VSInput
{
    float4 Position : POSITION;
    float2 Texture  : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture  : TEXCOORD0;
};

PSInput main(VSInput vsi)
{
    PSInput psi;

    psi.Position= vsi.Position;
    psi.Texture = vsi.Texture;

    return psi;
}
"u8;
}
