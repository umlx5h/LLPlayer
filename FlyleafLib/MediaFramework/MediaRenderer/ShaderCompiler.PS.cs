/*
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
Texture2D       IccLut          : register(t4);

struct ConfigData
{
    int coefsIndex;

    float hdrBrightness;
    float brightness;
    float contrast;
    float hue;
    float saturation;

    float uvOffset;

    float splineSrcPivot;
    float splineDstPivot;
    float splinePa;
    float splineSlope;
    float splineQa;
    float splineQb;

    float pqScale;
    float sourceMinNits;
    float sourcePeakNits;
    float targetMinNits;
    float targetPeakNits;
};

cbuffer         Config          : register(b0)
{
    ConfigData Config;
};

SamplerState    Sampler         : register(s0);

#if defined(dPano360)
cbuffer PanoConfig : register(b1)
{
    float4 panoParams;   // rotationX, rotationY, zoom, fov
    float  aspectRatio;
};

#define PI_PANO 3.1415926535897932384626433832795
#define DEG2RAD_PANO 0.01745329251994329576923690768489

inline float3 PanoRotateXY(float3 p, float2 angle)
{
    float2 c = cos(angle);
    float2 s = sin(angle);
    p = float3(p.x, c.x * p.y + s.x * p.z, -s.x * p.y + c.x * p.z);
    return float3(c.y * p.x + s.y * p.z, p.y, -s.y * p.x + c.y * p.z);
}

inline float2 PanoProject(float2 uv)
{
    float2 sampleUV = uv - 0.5;
    float hfovRad = panoParams.w * DEG2RAD_PANO;
    float vfovRad = 2.0 * atan(tan(hfovRad * 0.5) / aspectRatio);
    float3 camDir = normalize(float3(
        -sampleUV.x * tan(hfovRad * 0.5),
         sampleUV.y * tan(vfovRad * 0.5),
         panoParams.z));
    float3 camRot = float3(
        (panoParams.x - 0.5) * 2.0 * PI_PANO,
        (panoParams.y - 0.5) * PI_PANO, 0.0);
    float3 rd = normalize(PanoRotateXY(camDir, camRot.yx));
    return float2(atan2(rd.z, rd.x) + PI_PANO, acos(-rd.y)) / float2(2.0 * PI_PANO, PI_PANO);
}
#endif

#if defined(dICC)
#define ICC_LUT_SIZE 33.0

inline float3 ApplyICC(float3 c)
{
    c = saturate(c);

    float b = c.b * (ICC_LUT_SIZE - 1.0);

    float b0 = floor(b);
    float b1 = min(b0 + 1.0, ICC_LUT_SIZE - 1.0);
    float bf = b - b0;

    float r = c.r * (ICC_LUT_SIZE - 1.0);
    float g = c.g * (ICC_LUT_SIZE - 1.0);

    float width = ICC_LUT_SIZE * ICC_LUT_SIZE;

    float2 uv0 = float2(
        (b0 * ICC_LUT_SIZE + r + 0.5) / width,
        (g + 0.5) / ICC_LUT_SIZE);

    float2 uv1 = float2(
        (b1 * ICC_LUT_SIZE + r + 0.5) / width,
        (g + 0.5) / ICC_LUT_SIZE);

    float3 c0 = IccLut.SampleLevel(Sampler, uv0, 0).rgb;
    float3 c1 = IccLut.SampleLevel(Sampler, uv1, 0).rgb;

    return lerp(c0, c1, bf);
}
#endif

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
    #if defined(dYUV16)
        // P010 limited, sampled as R16_UNorm
        yuv.x  -= 0.0625;
        yuv.yz -= 0.5;
        yuv *= 257.0 / 256.0;
    #else
        // 8-bit limited, sampled as R8_UNorm
        yuv.x  -= 16.0 / 255.0;
        yuv.yz -= 128.0 / 255.0;

    #endif

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

#if defined(dBT1886ToLinear)
inline float3 BT1886ToLinear(float3 c)
{
    return pow(max(c, 0.0), 2.4);
}
#endif

#if defined(dBT2020)
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

inline float3 GamutCompress709(float3 c, float knee)
{
    static const float3 luma709 =
        float3(0.2126, 0.7152, 0.0722);

    float y = saturate(dot(c, luma709));

    float lo = min(c.r, min(c.g, c.b));
    float hi = max(c.r, max(c.g, c.b));

    float limit = 1e20;

    if (hi > y)
        limit = min(limit, (1.0 - y) / (hi - y));

    if (lo < y)
        limit = min(limit, y / (y - lo));

    if (limit == 1e20)
        return c;

    float usage = 1.0 / max(limit, 1e-6);

    if (usage <= knee)
        return c;

    float k = 1.0 - knee;

    float mappedUsage =
        1.0 - (k * k) /
        (usage + 1.0 - 2.0 * knee);

    float scale = mappedUsage / usage;

    return y.xxx + (c - y.xxx) * scale;
}
#endif

#if defined(dPQ) || defined(dPQSpline)
static const float ST2084_m1 = 0.1593017578125;
static const float ST2084_m2 = 78.84375;
static const float ST2084_c1 = 0.8359375;
static const float ST2084_c2 = 18.8515625;
static const float ST2084_c3 = 18.6875;

inline float3 PQToLinear(float3 rgb, float factor)
{
    rgb  = max(rgb, 0.0);
    rgb  = pow(rgb, 1.0 / ST2084_m2);
    rgb  = max(rgb - ST2084_c1, 0.0) / (ST2084_c2 - ST2084_c3 * rgb);
    rgb  = pow(rgb, 1.0 / ST2084_m1);
    rgb *= factor;
    return rgb;
}
#endif

#if defined(dPQSpline)
inline float PQToNits(float pq)
{
    pq = max(pq, 0.0);

    float p = pow(pq, 1.0 / ST2084_m2);
    p = max(p - ST2084_c1, 0.0) /
        (ST2084_c2 - ST2084_c3 * p);

    return pow(p, 1.0 / ST2084_m1) * 10000.0;
}

inline float NitsToPQ(float nits)
{
    float x = saturate(nits / 10000.0);

    x = pow(x, ST2084_m1);
    x = (ST2084_c1 + ST2084_c2 * x) /
        (1.0 + ST2084_c3 * x);

    return pow(x, ST2084_m2);
}

inline float ToneSpline(float x)
{
    x -= Config.splineSrcPivot;

    if (x > 0.0)
    {
        x = ((Config.splineQa * x +
              Config.splineQb) * x +
              Config.splineSlope) * x;
    }
    else
    {
        x = (Config.splinePa * x +
             Config.splineSlope) * x;
    }

    return x + Config.splineDstPivot;
}
#endif

#if defined(dHLG)
inline float3 HLGInverseOETF(float3 c)
{
    const float A = 0.17883277;
    const float B = 0.28466892;
    const float C = 0.55991073;

    // Don't saturate upper values; HLG allows headroom > 1
    c = max(c, 0.0);

    float3 lo = c * c / 3.0;
    float3 hi = (exp((c - C) / A) + B) / 12.0;

    return lerp(lo, hi, step(0.5, c));
}

inline float3 HLGToDisplayLinear(float3 c, float targetPeakNits)
{
    static const float3 luma2020 = float3(0.2627, 0.6780, 0.0593);

    c = HLGInverseOETF(c);

    float y = dot(c, luma2020);

    if (y <= 0.0)
        return 0.0;

    float gamma = 1.2 + 0.42 * log10(targetPeakNits / 1000.0);

    // Normalized display-linear result.
    // Peak white stays 1.0.
    c *= pow(y, gamma - 1.0);

    return c;
}
#endif

#if defined(dFilters)
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
inline float Contrast(float y, float contrast)
{
    if (contrast == 1.0)
        return y;

    y = saturate((y - 0.0625) / 0.85546875);

    y = lerp(
        y,
        pow(y, 2.0 - contrast),
        smoothstep(0.0, 1.0, y));

    return 0.0625 + y * 0.85546875;
}
#pragma warning( enable: 4000 )
#endif

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture  : TEXCOORD0;
};

float4 main(PSInput input) : SV_TARGET
{
    float4 color;
#if defined(dPano360)
    input.Texture = PanoProject(input.Texture);
#endif
"u8;

    static ReadOnlySpan<byte> PS_FOOTER => @"
    float3 c = color.rgb;

#if defined(dYUVLimited)
    #if defined(dFilters)
        c.x = Contrast(c.x, Config.contrast);
    #endif
	c = YUVToRGBLimited(c);
#elif defined(dYUVFull)
    #if defined(dFilters)
        c.x = Contrast(c.x, Config.contrast);
    #endif
	c = YUVToRGBFull(c);
#endif

#if defined(dICC)
    c = ApplyICC(c);
#elif defined(dBT1886ToLinear)
    c = BT1886ToLinear(c);
#elif defined(dHLG)
    c = HLGToDisplayLinear(c, Config.targetPeakNits);
#elif defined(dPQ)
    c = PQToLinear(c, Config.pqScale);
#elif defined(dPQSpline)
    c = PQToLinear(c, 10000.0);

    static const float3 luma2020 = float3(0.2627, 0.6780, 0.0593);
    float y = dot(c, luma2020);

    if (y > 0.0)
    {
        float toneY     = clamp(y, Config.sourceMinNits, Config.sourcePeakNits);
        float mappedPQ  = saturate(ToneSpline(NitsToPQ(toneY)));
        float mappedY   = PQToNits(mappedPQ);
        float u         = saturate((mappedY - Config.targetMinNits) / (Config.targetPeakNits - Config.targetMinNits));
        c *= u / y;
    }
#endif

#if defined(dBT2020)
    float hdrY = dot(c, float3(0.2627, 0.6780, 0.0593));

    if (hdrY > 0.0)
    {
        float u         = saturate(hdrY);
        float gain      = Config.hdrBrightness;
        float adjustedY = (gain * u) / (1.0 + (gain - 1.0) * u);
        c *= adjustedY / hdrY;
    }

    c = Gamut2020To709(c);
    #if !defined(dBT1886ToLinear)
        c /= max(max(c.r, max(c.g, c.b)), 1.0); // HDR Brightness could cause this
        c = GamutCompress709(c, 1.0);
    #endif
    c = saturate(c);
    c = pow(c, 1.0 / 2.2);
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
