/*
LinearToSRGB | SRGBToLinear:
    Simplified version (slightly better performance and on dark colors, but not accurate enough) | possible expose to config
    c = pow(c, 1.0 / 2.2); | c = pow(c, 2.2);

HABLE_DEFAULT
    TBR: whitepoint, if should be 4.8 and possible expose to config

Chroma Location / Sampling
    Small improvement but not performance penalty

Up/Down Scaling
    High performance penalty and difficult implementation for good quality
    Use D3D11VA proprietary and add shader support

Filters
    Contrast might still not properly working for RGB or YUV full?*
*/

namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_HEADER => @"
#pragma warning( disable: 3571 )

Texture2D		Texture1		: register(t0);
Texture2D		Texture2		: register(t1);
Texture2D		Texture3		: register(t2);
Texture2D		Texture4		: register(t3);

struct ConfigData
{
    int coefsIndex;

    float brightness;
    float contrast;
    float hue;
    float saturation;

    float uvOffset;
    int tonemap;
    float hdrtone;
};

cbuffer         Config          : register(b0)
{
    ConfigData Config;
};

SamplerState    Sampler         : register(s0);

inline float3 LinearToSRGB(float3 c)
{
    float3 srgbLo = c * 12.92;
    float3 srgbHi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    float3 threshold = step(0.0031308, c);

    return lerp(srgbLo, srgbHi, threshold);
}

inline float3 SRGBToLinear(float3 c)
{
    float3 linearLo = c / 12.92;
    float3 linearHi = pow((c + 0.055) / 1.055, 2.4);
    float3 threshold = step(0.04045, c);
    
    return lerp(linearLo, linearHi, threshold);
}

inline float3 Gamut2020To709(float3 c)
{
    static const float3x3 mat = 
    {
         1.6605, -0.5876, -0.0728,
        -0.1246,  1.1329, -0.0083,
        -0.0182, -0.1006,  1.1187
    };
    return mul(mat, c);
}

#if defined(dYUVLimited)
static const float3x3 coefs[3] =
{
    // 0: BT.2020 (Limited)
    {
        1.16438356,  0.00000000,  1.67867410,
        1.16438356, -0.18732601, -0.65042418,
        1.16438356,  2.14177196,  0.00000000
    },
    // 1: BT.709  (Limited)
    {
        1.16438356,  0.00000000,  1.79274107,
        1.16438356, -0.21324861, -0.53290933,
        1.16438356,  2.11240179,  0.00000000
    },
    // 2: BT.601  (Limited)
    {
        1.16438356,  0.00000000,  1.59602678,
        1.16438356, -0.39176160, -0.81296823,
        1.16438356,  2.01723214,  0.00000000
    }
};

inline float3 YUVToRGBLimited(float3 yuv)
{
    yuv.x  -= 0.0625;
    yuv.yz -= 0.5;
    return mul(coefs[Config.coefsIndex], yuv);
}
#elif defined(dYUVFull)
static const float3x3 coefs[3] =
{
    // 0: BT.2020 (Full)
    {
        1.00000000,  0.00000000,  1.47460000,
        1.00000000, -0.16455313, -0.57135313,
        1.00000000,  1.88140000,  0.00000000
    },
    // 1: BT.709  (Full)
    {
        1.00000000,  0.00000000,  1.57480000,
        1.00000000, -0.18732600, -0.46812400,
        1.00000000,  1.85560000,  0.00000000
    },
    // 2: BT.601  (Full)
    {
        1.00000000,  0.00000000,  1.40200000,
        1.00000000, -0.34413600, -0.71413600,
        1.00000000,  1.77200000,  0.00000000
    }
};

inline float3 YUVToRGBFull(float3 yuv)
{
    yuv.yz -= 0.5;
    return mul(coefs[Config.coefsIndex], yuv);
}
#else
// TODO: RGBLimitedToFull + Linears (transfer from .cs)
static const float rgbOffset = 16.0 / 255.0;
static const float rgbScale = 255.0 / 219.0;
#endif

#if defined(dPQToLinear) || defined(dHLGToLinear)
static const float ST2084_m1 = 0.1593017578125;
static const float ST2084_m2 = 78.84375;
static const float ST2084_c1 = 0.8359375;
static const float ST2084_c2 = 18.8515625;
static const float ST2084_c3 = 18.6875;

inline float3 PQToLinear(float3 rgb, float factor)
{
    rgb  = pow(rgb, 1.0 / ST2084_m2);
    rgb  = max(rgb - ST2084_c1, 0.0) / (ST2084_c2 - ST2084_c3 * rgb);
    rgb  = pow(rgb, 1.0 / ST2084_m1);
    rgb *= factor;
    return rgb;
}

inline float3 LinearToPQ(float3 rgb, float divider)
{
    rgb /= divider;
    rgb  = pow(rgb, ST2084_m1);
    rgb  = (ST2084_c1 + ST2084_c2 * rgb) / (1.0f + ST2084_c3 * rgb);
    rgb  = pow(rgb, ST2084_m2);
    return rgb;
}
#endif

#if defined(dHLGToLinear)
inline float3 HLGInverse(float3 rgb)
{
    const float A = 0.17883277;
    const float B = 0.28466892;
    const float C = 0.55991073;

    rgb = (rgb <= 0.5)
        ? rgb * rgb * 4.0
        : (exp((rgb - C) / A) + B);

    return rgb;
}

inline float3 HLGToLinear(float3 rgb)
{
    static const float3 ootf_2020 = float3(0.2627, 0.6780, 0.0593);

    rgb = HLGInverse(rgb);
    float ootf_ys = 2000.0f * dot(ootf_2020, rgb);
    rgb *= pow(ootf_ys, 0.2f);
    return rgb;
}
#endif

#if defined(dTone)
inline float3 ToneAces(float3 x)
{
    const float A = 2.51;
    const float B = 0.03;
    const float C = 2.43;
    const float D = 0.59;
    const float E = 0.14;

    return (x * (A * x + B)) / (x * (C * x + D) + E);
}

inline float3 ToneHable(float3 x)
{
    const float A = 0.15f;
    const float B = 0.5f;
    const float C = 0.1f;
    const float D = 0.2f;
    const float E = 0.02f;
    const float F = 0.3f;

    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}
static const float3 HABLE_DEFAULT = ToneHable(11.2);

inline float3 ToneReinhard(float3 x)
{
    return x * (1.0 + x / 4.84) / (x + 1.0);
}
#endif

#pragma warning( disable: 4000 )
inline float3 Hue(float3 rgb, float angle)
{
    if (angle == 0)
        return rgb;

    static const float3x3 hueBase = float3x3(
        0.299,  0.587,  0.114,
        0.299,  0.587,  0.114,
        0.299,  0.587,  0.114
    );

    static const float3x3 hueCos = float3x3(
         0.701, -0.587, -0.114,
        -0.299,  0.413, -0.114,
        -0.300, -0.588,  0.886
    );
    
    static const float3x3 hueSin = float3x3(
         0.168,  0.330, -0.497,
        -0.328,  0.035,  0.292,
         1.250, -1.050, -0.203
    );

    float c = cos(angle);
    float s = -sin(angle);

    return mul(hueBase + c * hueCos + s * hueSin, rgb);
}

inline float3 Saturation(float3 rgb, float saturation)
{
    if (saturation == 1.0)
        return rgb;

    static const float3 kBT709 = float3(0.2126, 0.7152, 0.0722);

    float luminance = dot(rgb, kBT709);
    return lerp(luminance.rrr, rgb, saturation);
}
#pragma warning( enable: 4000 )

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture  : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    float4 color;
"u8;

    static ReadOnlySpan<byte> PS_FOOTER => @"
    float3 c = color.rgb;

#if defined(dYUVLimited)
    #if defined(dFilters)
    if (Config.contrast != 1.0)
        c.x = lerp(c.x, pow(c.x, 2.0 - Config.contrast), smoothstep(0.0, 1.0, c.x));
    #endif
	c = YUVToRGBLimited(c);
#elif defined(dYUVFull)
    #if defined(dFilters)
    if (Config.contrast != 1.0)
        c.x = lerp(c.x, pow(c.x, 2.0 - Config.contrast), smoothstep(0.0, 1.0, c.x));
    #endif
	c = YUVToRGBFull(c);
#endif

#if defined(dBT2020)
    c = SRGBToLinear(c); // TODO: transferfunc
	c = Gamut2020To709(c);
	c = saturate(c);
	c = LinearToSRGB(c);
#else

    #if defined(dPQToLinear)
	c = PQToLinear(c, Config.hdrtone);
    #elif defined(dHLGToLinear)
	c = HLGToLinear(c);
	c = LinearToPQ(c, 1000.0);
	c = PQToLinear(c, Config.hdrtone);
    #endif

    #if defined(dTone)
    [branch]
	if (Config.tonemap == 2)
	{
		c = ToneHable(c) / HABLE_DEFAULT;
        c = saturate(c);
		c = Gamut2020To709(c);
        c = saturate(c);
		c = LinearToSRGB(c);
	}
	else if (Config.tonemap == 3)
	{
		c = ToneReinhard(c);
        c = saturate(c);
		c = Gamut2020To709(c);
        c = saturate(c);
		c = LinearToSRGB(c);
	}
	else if (Config.tonemap == 1)
	{
		c = ToneAces(c);
        c = saturate(c);
		c = Gamut2020To709(c);
        c = saturate(c);
		c = pow(c, 0.27);
	}
    else
    {
        c = LinearToSRGB(c);
    }
    #endif

#endif

#if defined(dFilters)
    #if !defined(dYUVLimited) && !defined(dYUVFull)
    c = (c - 0.5) * (2.0 - Config.contrast) + 0.5;
    #endif
    c += Config.brightness;
    c  = Hue(c, Config.hue);
    c  = Saturation(c, Config.saturation);
#endif
    
    return saturate(float4(c * color.a, color.a));
}
"u8;
}
