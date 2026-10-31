namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_BT2020 => @"
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

#if defined(dBT1886ToLinear)
	inline float3 BT1886ToLinear(float3 c)
	{
		return pow(max(c, 0.0), 2.4);
	}

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
#endif
"u8;
}
