namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_HEADER => @"
#pragma warning( disable: 3571 )

Texture2D		Texture1		: register(t0);
Texture2D		Texture2		: register(t1);
Texture2D		Texture3		: register(t2);
Texture2D		Texture4		: register(t3);
SamplerState    Sampler         : register(s0);

struct ConfigData
{
    int coefsIndex;

    float brightness;
    float contrast;
    float hue;
    float saturation;

    float uvOffset;
};

cbuffer Config : register(b0)
{
    ConfigData Config;
};

#if defined(dYUVLimited)
static const float3x3 coefs[3] =
{
    {	// 0: BT.2020 (Limited)
        1.16438356,  0.00000000,  1.67867410,
        1.16438356, -0.18732601, -0.65042418,
        1.16438356,  2.14177196,  0.00000000
    },
    {	// 1: BT.709  (Limited)
        1.16438356,  0.00000000,  1.79274107,
        1.16438356, -0.21324861, -0.53290933,
        1.16438356,  2.11240179,  0.00000000
    },
    {	// 2: BT.601  (Limited)
        1.16438356,  0.00000000,  1.59602678,
        1.16438356, -0.39176160, -0.81296823,
        1.16438356,  2.01723214,  0.00000000
    }
};

inline float3 YUVToRGBLimited(float3 yuv)
{
    #if defined(dYUV16)
        // P010 limited, sampled as R16_UNorm. Convert 10-bit MSB-aligned UNorm to the equivalent 8-bit-normalized code domain.
        yuv *= 257.0 / 256.0;
    #endif
    
    yuv.x  -= 16.0 / 255.0;
    yuv.yz -= 128.0 / 255.0;

    return mul(coefs[Config.coefsIndex], yuv);
}
#elif defined(dYUVFull)
static const float3x3 coefs[3] =
{
    {	// 0: BT.2020 (Full)
        1.00000000,  0.00000000,  1.47460000,
        1.00000000, -0.16455313, -0.57135313,
        1.00000000,  1.88140000,  0.00000000
    },
    {	// 1: BT.709  (Full)
        1.00000000,  0.00000000,  1.57480000,
        1.00000000, -0.18732600, -0.46812400,
        1.00000000,  1.85560000,  0.00000000
    },
    {	// 2: BT.601  (Full)
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

// FILTERS

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

// ICC PROFILES

#if defined(dICC)
Texture2D       IccLut          : register(t4);

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

// PANORAMIC (360)

#if defined(dPano360)
struct PanoData
{
    float4 panoParams;   // rotationX, rotationY, zoom, fov
    float  aspectRatio;
};

cbuffer PanoConfig : register(b3)
{
    PanoData Pano;
};

inline float3 PanoRotateXY(float3 p, float2 angle)
{
    float2 c = cos(angle);
    float2 s = sin(angle);
    p = float3(p.x, c.x * p.y + s.x * p.z, -s.x * p.y + c.x * p.z);
    return float3(c.y * p.x + s.y * p.z, p.y, -s.y * p.x + c.y * p.z);
}

inline float2 PanoProject(float2 uv)
{
    const float PI_PANO      = 3.1415926535897932384626433832795;
    const float DEG2RAD_PANO = 0.01745329251994329576923690768489;

    float2 sampleUV = uv - 0.5;
    float hfovRad = Pano.panoParams.w * DEG2RAD_PANO;
    float vfovRad = 2.0 * atan(tan(hfovRad * 0.5) / Pano.aspectRatio);
    float3 camDir = normalize(float3(
        -sampleUV.x * tan(hfovRad * 0.5),
         sampleUV.y * tan(vfovRad * 0.5),
         Pano.panoParams.z));

    float3 camRot = float3(
        (Pano.panoParams.x - 0.5) * 2.0 * PI_PANO,
        (Pano.panoParams.y - 0.5) * PI_PANO, 0.0);

    float3 rd = normalize(PanoRotateXY(camDir, camRot.yx));

    return float2(atan2(rd.z, rd.x) + PI_PANO, acos(-rd.y)) / float2(2.0 * PI_PANO, PI_PANO);
}
#endif

"u8;

    static ReadOnlySpan<byte> PS_MAIN => @"
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
}
