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

// BT2020 (HDR)

#if defined(dBT2020)
Texture3D       GamutLut        : register(t5);

struct HDRData
{
    float splineSrcPivot;
    float splineDstPivot;
    float splinePa;
    float splineSlope;
    float splineQa;
    float splineQb;

    float sourceMinNits;
    float sourcePeakNits;
    float targetMinNits;
    float targetPeakNits;

    int   nativeOutput;
    float hlgGamma;
    float bt1886BlackRoot;
    float bt1886InvRange;
    float gamutMinPQ;
    float gamutInvRangePQ;
};

cbuffer HDRConfig : register(b1)
{
    HDRData HDR;
};

static const float3 LUMA_2020 = float3(0.2627, 0.6780, 0.0593);

static const float ST2084_m1 = 0.1593017578125;
static const float ST2084_m2 = 78.84375;
static const float ST2084_c1 = 0.8359375;
static const float ST2084_c2 = 18.8515625;
static const float ST2084_c3 = 18.6875;

static const float GAMUT_INV_2PI        = 0.15915494309189533577;
static const float GAMUT_LUT_INTENSITY  = 48.0;
static const float GAMUT_LUT_CHROMA     = 32.0;
static const float GAMUT_LUT_HUE        = 256.0;

static const float3x3 Gamut2020ToLms =
{
    0.41203639, 0.52391191, 0.06405498,
    0.16666022, 0.72039521, 0.11294612,
    0.02411236, 0.07547496, 0.90040794
};

static const float3x3 GamutLmsTo2020 =
{
     3.43681483, -2.50677380,  0.06995193,
    -0.79105824,  1.98360167, -0.19254483,
    -0.02572681, -0.09914177,  1.12487414
};

static const float3x3 GamutLmsTo709 =
{
     6.17353266, -5.32089882,  0.14735489,
    -1.32403191,  2.56026977, -0.23623862,
    -0.01159839, -0.26492145,  1.27652634
};

static const float3x3 GamutLmsToIpt =
{
    0.4000,  0.4000,  0.2000,
    4.4550, -4.8510,  0.3960,
    0.8056,  0.3572, -1.1628
};

static const float3x3 GamutIptToLms =
{
    1.0000000,  0.0975689,  0.2052260,
    1.0000000, -0.1138760,  0.1332170,
    1.0000000,  0.0326151, -0.6768870
};

inline float TargetBlack()
{
    return saturate(HDR.targetMinNits / max(HDR.targetPeakNits, 1e-6));
}

inline float3 ApplyDisplayBlack(float3 c)
{
    float black = TargetBlack();
    return black.xxx + c * (1.0 - black);
}

inline float3 Linear2020To709(float3 c)
{
    static const float3x3 mat = 
    {
         1.6605, -0.5876, -0.0728,
        -0.1246,  1.1329, -0.0083,
        -0.0182, -0.1006,  1.1187
    };
    return mul(mat, c);
}

inline float3 LinearToBT1886(float3 c)
{
    c = pow(max(c, 0.0), 1.0 / 2.4);
    return saturate((c - HDR.bt1886BlackRoot) * HDR.bt1886InvRange);
}

inline float Luma2020(float3 c)
{
    return dot(c, LUMA_2020);
}

inline float3 GamutPQOetf(float3 c)
{
    c = pow(max(c, 0.0), ST2084_m1);
    c = (ST2084_c1 + ST2084_c2 * c) / (1.0 + ST2084_c3 * c);
    return pow(c, ST2084_m2);
}

inline float3 GamutPQEotf(float3 c)
{
    c = pow(saturate(c), 1.0 / ST2084_m2);
    c = max(c - ST2084_c1, 0.0) / (ST2084_c2 - ST2084_c3 * c);
    return pow(c, 1.0 / ST2084_m1);
}

inline float3 Linear2020ToIPTPQ(float3 c)
{   // Input is target-peak-relative linear BT.2020

    float3 lms = mul(Gamut2020ToLms, c) * (HDR.targetPeakNits / 10000.0);
    return mul(GamutLmsToIpt, GamutPQOetf(lms));
}

inline float3 IPTPQToLinear709(float3 c)
{
    float3 lms = GamutPQEotf(mul(GamutIptToLms, c));
    float3 rgb = mul(GamutLmsTo709, lms);

    // Back from 10k-normalized light to target-peak-relative linear RGB
    return rgb * (10000.0 / max(HDR.targetPeakNits, 1e-6));
}

inline float3 GamutMap2020To709(float3 c)
{   // physical target-relative linear BT.2020 -> physical target-relative linear BT.709

    float3 ipt = Linear2020ToIPTPQ(c);

    float i = saturate((ipt.x - HDR.gamutMinPQ) * HDR.gamutInvRangePQ);
    float k = saturate(2.0 * length(ipt.yz));
    float h = saturate(atan2(ipt.z, ipt.y) * GAMUT_INV_2PI + 0.5);

    float3 uvw = float3(
        (i * (GAMUT_LUT_INTENSITY - 1.0) + 0.5) / GAMUT_LUT_INTENSITY,
        (k * (GAMUT_LUT_CHROMA    - 1.0) + 0.5) / GAMUT_LUT_CHROMA,
        (h * (GAMUT_LUT_HUE       - 1.0) + 0.5) / GAMUT_LUT_HUE);

    float3 mapped = GamutLut.SampleLevel(Sampler, uvw, 0).rgb;
    mapped.yz -= 32768.0 / 65535.0;

    float targetBlack = TargetBlack();

    return clamp(IPTPQToLinear709(mapped), targetBlack.xxx, 1.0);
}

	// BT.2020 SDR
	
	#if defined(dBT1886ToLinear)
	inline float3 BT1886ToLinear(float3 c)
	{
		return pow(max(c, 0.0), 2.4);
	}
	
	// BT.2020 HLG
	#elif defined(dHLG)
	inline float3 HLGInverseOETF(float3 c)
	{
		const float A = 0.17883277;
		const float B = 0.28466892;
		const float C = 0.55991073;
		
		c = max(c, 0.0); // Don't saturate upper values; HLG allows headroom > 1

		float3 lo = c * c / 3.0;
		float3 hi = (exp((c - C) / A) + B) / 12.0;

		return lerp(lo, hi, step(0.5, c));
	}

	inline float3 HLGToDisplayLinear(float3 c)
	{
		c = HLGInverseOETF(c);

		float y = Luma2020(c);

		if (y <= 0.0)
			return 0.0;

		c *= pow(y, HDR.hlgGamma - 1.0); // Normalized display-linear result. Peak white stays 1.0

		return c;
	}
	
	// BT.2020 PQ (HDR10 / HDR10+ / Dolby Vision)

	#elif defined(dPQSpline)
	inline float3 PQToLinear(float3 rgb, float factor)
	{
		rgb  = max(rgb, 0.0);
		rgb  = pow(rgb, 1.0 / ST2084_m2);
		rgb  = max(rgb - ST2084_c1, 0.0) / (ST2084_c2 - ST2084_c3 * rgb);
		rgb  = pow(rgb, 1.0 / ST2084_m1);
		rgb *= factor;
		return rgb;
	}

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
		if (nits <= 0.0)
			return 0.0;

		float x = saturate(nits / 10000.0);

		x = pow(x, ST2084_m1);
		x = (ST2084_c1 + ST2084_c2 * x) /
			(1.0 + ST2084_c3 * x);

		return pow(x, ST2084_m2);
	}

	inline float ToneSpline(float x)
	{
		x -= HDR.splineSrcPivot;

		if (x > 0.0)
		{
			x = ((HDR.splineQa * x +
				  HDR.splineQb) * x +
				  HDR.splineSlope) * x;
		}
		else
		{
			x = (HDR.splinePa * x +
				 HDR.splineSlope) * x;
		}

		return x + HDR.splineDstPivot;
	}
	
	inline float3 Absolute2020ToIPTPQ(float3 c)
	{	// Input is absolute linear BT.2020 in nits
		float3 lms = mul(Gamut2020ToLms, c) / 10000.0;
		return mul(GamutLmsToIpt, GamutPQOetf(lms));
	}
	
	inline float3 IPTPQToAbsolute2020(float3 c)
	{
		float3 lms = GamutPQEotf(mul(GamutIptToLms, c));
		return mul(GamutLmsTo2020, lms) * 10000.0;
	}

	inline float3 IPTPQToLinear2020(float3 c)
	{   // Output is target-peak-relative linear BT.2020

		return IPTPQToAbsolute2020(c) / max(HDR.targetPeakNits, 1e-6);
	}
	
	inline float GamutToneHull(float i)
	{
		return ((i - 6.0) * i + 9.0) * i;
	}

	inline float3 ToneMapAbsolute2020ToIPTPQ(float3 c)
	{   // Input is absolute linear BT.2020 in nits; output is tone-mapped IPTPQc4

		float3 ipt = Absolute2020ToIPTPQ(c);
		float sourceI = ipt.x;

		// ToneSpline is itself a PQ-domain mapping, so apply it directly to I
		float targetI = saturate(ToneSpline(sourceI));
		ipt.x = targetI;

		if (sourceI > 1e-7 && targetI > 1e-7)
		{
			float sourceHull = GamutToneHull(sourceI);
			float targetHull = GamutToneHull(targetI);
			float scaleI     = sourceI / targetI;
			float scaleHull  = sourceHull > 1e-7 ? targetHull / sourceHull : scaleI;

			// Scale P/T once for the new intensity while preserving IPT hue
			ipt.yz *= max(min(scaleI, scaleHull), 0.0);
		}
		else
		{
			ipt.yz = 0.0;
		}

		return ipt;
	}

	inline float3 ToneMapIPTPQ2020Absolute(float3 c)
	{   // Absolute linear BT.2020 nits -> absolute linear BT.2020 nits
		return IPTPQToAbsolute2020(ToneMapAbsolute2020ToIPTPQ(c));
	}

	inline float3 ToneMapIPTPQ2020(float3 c)
	{   // Absolute linear BT.2020 nits -> target-peak-relative linear BT.2020
		return IPTPQToLinear2020(ToneMapAbsolute2020ToIPTPQ(c));
	}

	inline float3 ToneMapLuma2020Absolute(float3 c)
	{   // Scalar spline mapping while keeping RGB chromaticity (I/O absolute nits)

		float y = Luma2020(c);
		if (y <= 0.0)
			return c;

		float toneY    = clamp(y, HDR.sourceMinNits, HDR.sourcePeakNits);
		float mappedPQ = saturate(ToneSpline(NitsToPQ(toneY)));
		float mappedY  = PQToNits(mappedPQ);

		return c * (mappedY / y);
	}

	inline float3 ToneMapLuma2020ToTarget(float3 c)
	{   // Scalar spline mapping to physical target-relative BT.2020.
		// Remove/reapply the display-black pedestal only in scalar luminance,
		// never by subtracting it from individual RGB channels.

		float y     = Luma2020(c);
		float black = TargetBlack();

		if (y <= 0.0)
			return black.xxx;

		float toneY    = clamp(y, HDR.sourceMinNits, HDR.sourcePeakNits);
		float mappedPQ = saturate(ToneSpline(NitsToPQ(toneY)));
		float mappedY  = PQToNits(mappedPQ);

		float mappedRelative = saturate(mappedY / max(HDR.targetPeakNits, 1e-6));
		float u = saturate((mappedRelative - black) / max(1.0 - black, 1e-6));

		return ApplyDisplayBlack(c * (u / y));
	}

		// Dolby Vision (PQ path)
		
		#if defined(dDovi)
		cbuffer DoviConfig : register(b2)
		{
			float4 Dovi[187];
		};

		#define DOVI_SAMPLE_SCALE       Dovi[0].x
		#define DOVI_IDENTITY_RESHAPE   Dovi[0].y
		#define DOVI_YCC_BASE           1
		#define DOVI_LINEAR_2020_BASE   4
		#define DOVI_LINEAR_709_BASE    7
		#define DOVI_COMPONENT_BASE     10
		#define DOVI_COMPONENT_VECTORS  59
		#define DOVI_MMR_BASE           11
		
		inline float DoviReshapeComponent(float3 sig, int component)
		{
			int b = DOVI_COMPONENT_BASE + component * DOVI_COMPONENT_VECTORS;
			float s = sig[component];

			float4 p0 = Dovi[b + 1];
			float4 p1 = Dovi[b + 2];

			int piece = 0;
			piece += s >= p0.x ? 1 : 0;
			piece += s >= p0.y ? 1 : 0;
			piece += s >= p0.z ? 1 : 0;
			piece += s >= p0.w ? 1 : 0;
			piece += s >= p1.x ? 1 : 0;
			piece += s >= p1.y ? 1 : 0;
			piece += s >= p1.z ? 1 : 0;

			float4 coeffs = Dovi[b + 3 + piece];

			if (coeffs.w == 0.0)
			{
				s = (coeffs.z * s + coeffs.y) * s + coeffs.x;
			}
			else
			{
				int mmr = b + DOVI_MMR_BASE + (int)coeffs.y;
				int order = (int)coeffs.w;

				float4 sigX;
				sigX.xyz = sig.xxy * sig.yzz;
				sigX.w = sigX.x * sig.z;

				s = coeffs.x;
				s += dot(Dovi[mmr + 0].xyz, sig);
				s += dot(Dovi[mmr + 1], sigX);

				if (order >= 2)
				{
					float3 sig2 = sig * sig;
					float4 sigX2 = sigX * sigX;

					s += dot(Dovi[mmr + 2].xyz, sig2);
					s += dot(Dovi[mmr + 3], sigX2);

					if (order >= 3)
					{
						s += dot(Dovi[mmr + 4].xyz, sig2 * sig);
						s += dot(Dovi[mmr + 5], sigX2 * sigX);
					}
				}
			}

			return clamp(s, Dovi[b].x, Dovi[b].y);
		}

		inline float3 DoviDecodeYCC(float3 c)
		{
			float3 sig = saturate(c * DOVI_SAMPLE_SCALE);
			float3 reshaped = sig;

			if (DOVI_IDENTITY_RESHAPE == 0.0)
			{   // Common Profile 7 streams carry an exact identity reshape. Skip all pivot/polynomial work in that case
				reshaped = float3(
					DoviReshapeComponent(sig, 0),
					DoviReshapeComponent(sig, 1),
					DoviReshapeComponent(sig, 2));
			}

			float4 v = float4(reshaped, 1.0);

			return float3(
				dot(Dovi[DOVI_YCC_BASE + 0], v),
				dot(Dovi[DOVI_YCC_BASE + 1], v),
				dot(Dovi[DOVI_YCC_BASE + 2], v));
		}

		inline float DoviLuma(float3 linearRgb)
		{
			float3 luma = float3(
				Dovi[DOVI_LINEAR_2020_BASE + 0].w,
				Dovi[DOVI_LINEAR_2020_BASE + 1].w,
				Dovi[DOVI_LINEAR_2020_BASE + 2].w);
			return dot(luma, linearRgb);
		}

		inline float3 DoviTo2020(float3 linearRgb)
		{
			return float3(
				dot(Dovi[DOVI_LINEAR_2020_BASE + 0].xyz, linearRgb),
				dot(Dovi[DOVI_LINEAR_2020_BASE + 1].xyz, linearRgb),
				dot(Dovi[DOVI_LINEAR_2020_BASE + 2].xyz, linearRgb));
		}

		inline float3 DoviTo709(float3 linearRgb)
		{   // Not used
			return float3(
				dot(Dovi[DOVI_LINEAR_709_BASE + 0].xyz, linearRgb),
				dot(Dovi[DOVI_LINEAR_709_BASE + 1].xyz, linearRgb),
				dot(Dovi[DOVI_LINEAR_709_BASE + 2].xyz, linearRgb));
		}
		#endif
	#endif
#endif

// ============ MAIN ============

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

#if defined(dDovi)
    c = DoviDecodeYCC(c);
#elif defined(dYUVLimited)
    #if defined(dFilters) && !defined(dHDRDetect)
        c.x = Contrast(c.x, Config.contrast);
    #endif
    c = YUVToRGBLimited(c);
#elif defined(dYUVFull)
    #if defined(dFilters) && !defined(dHDRDetect)
        c.x = Contrast(c.x, Config.contrast);
    #endif
    c = YUVToRGBFull(c);
#endif

#if defined(dHDRDetect)
    c = PQToLinear(c, 10000.0);

    #if defined(dDovi)
        float y = DoviLuma(c);
    #else
        static const float3 luma2020 = float3(0.2627, 0.6780, 0.0593);
        float y = dot(c, luma2020);
    #endif

    return float4(NitsToPQ(y), 0.0, 0.0, 1.0);

#else // HDRDetect

#if defined(dICC)
    c = ApplyICC(c);

#elif defined(dBT1886ToLinear)
    c = BT1886ToLinear(c);

#elif defined(dHLG)
    c = HLGToDisplayLinear(c);

    if (HDR.nativeOutput != 0)
        c *= HDR.targetPeakNits;

#elif defined(dPQSpline)
    c = PQToLinear(c, 10000.0); // absolute linear light in nits

    // From here on HDR10/HDR10+/Dovi share one domain | absolute linear BT.2020 nits
    #if defined(dDovi)
        c = DoviTo2020(c);
    #endif

    if (HDR.nativeOutput != 0)
    {
        if (HDR.sourcePeakNits > HDR.targetPeakNits)
        {
            c = ToneMapIPTPQ2020Absolute(c); // Source exceeds the HDR display | tone-map in IPTPQc4 but stay in absolute nits
        }
        else if (HDR.sourceMinNits < HDR.targetMinNits)
        {
            c = ToneMapLuma2020Absolute(c); // Peak fits; only the lower end needs adaptation. Keep chromaticity scalar
        }

        // Else the complete source range fits | preserve absolute HDR luminance
    }
    else
    {
        if (HDR.sourcePeakNits > HDR.targetPeakNits)
        {
            c = ToneMapIPTPQ2020(c); // Real HDR -> SDR compression. Output is already physical target-relative BT.2020.
        }
        else
        {
            // Peak fits, but keep the spline's perceptual brightness adaptation
            // Scalar mapping avoids unnecessary IPTPQ saturation changes, while target-black reconstruction keeps dark RGB inside the display volume
            c = ToneMapLuma2020ToTarget(c); // Peak fits, but keep the spline's perceptual brightness adaptation
        }
    }
#endif

#if defined(dBT2020)
    if (HDR.nativeOutput != 0)
    {
        c = Linear2020To709(c); // PQ/Dovi/HLG is already linear BT.2020 here
        c /= 80.0;              // scRGB is linear BT.709 and 1.0 = 80 nits
    }
    else
    {
        // PQ/Dovi is already in the physical target range. SDR/HLG is zero-black normalized, so lift it to the target black before gamut mapping
        #if !defined(dPQSpline)
            c = ApplyDisplayBlack(c);
        #endif

        c = GamutMap2020To709(c);
        c = LinearToBT1886(c);
    }
#endif

#if defined(dFilters)
    #if defined(dDovi) || (!defined(dYUVLimited) && !defined(dYUVFull))
        c = (c - 0.5) * (2.0 - Config.contrast) + 0.5;
    #endif
    c += Config.brightness;
    c  = Hue(c, Config.hue);
    c  = Saturation(c, Config.saturation);
#endif

#if defined(dBT2020)
    if (HDR.nativeOutput != 0)
        return float4(c * color.a, color.a);
#endif

    return saturate(float4(c * color.a, color.a));

#endif // HDRDetect
}
"u8;
}
