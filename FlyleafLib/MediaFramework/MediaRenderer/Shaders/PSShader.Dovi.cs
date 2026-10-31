namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_DOVI => @"
cbuffer DoviConfig : register(b2)
{
	float4 Dovi[190];
};

#define DOVI_SAMPLE_SCALE       Dovi[0].x
#define DOVI_IDENTITY_RESHAPE   Dovi[0].y
#define DOVI_YCC_BASE           1
#define DOVI_LINEAR_2020_BASE   4
#define DOVI_LINEAR_709_BASE    7
#define DOVI_COMPONENT_BASE     10
#define DOVI_COMPONENT_VECTORS  59
#define DOVI_MMR_BASE           11
		
// L2 trims carried in Dovi[187..189].
#define DOVI_TRIM_LEVEL      Dovi[187].x
#define DOVI_TRIM_GAIN       Dovi[188].x
#define DOVI_TRIM_OFFSET     Dovi[188].y
#define DOVI_TRIM_POWER      Dovi[188].z
#define DOVI_TRIM_CHROMA     Dovi[188].w
#define DOVI_TRIM_SAT        Dovi[189].x
#define DOVI_TRIM_MS         Dovi[189].y

// Chroma Weight is applied in IPTPQ after luminance and saturation trims.
// The source retains absolute BT.2020 nits; final gamut mapping remains separate.
inline float3 DoviTrimChromaWeight(float3 source2020Nits, float3 mappedIPT)
{
    float3 sourceIPT = Absolute2020ToIPTPQ(source2020Nits);
    float saturation = length(sourceIPT.yz);
    if (saturation < 1e-5)
        return IPTPQToAbsolute2020(mappedIPT);

    float intensityScale = max(0.0, 1.0 + (4.0 * DOVI_TRIM_CHROMA) * saturation);
    float chromaScale    = max(0.0, 1.0 + (3.0 * DOVI_TRIM_CHROMA) * saturation);

    float minNits = max(HDR.targetMinNits, 0.0);
    float maxNits = HDR.targetPeakNits;
    float adjustedNits = clamp(PQToNits(mappedIPT.x) * intensityScale, minNits, maxNits);
    float3 adjusted = float3(NitsToPQ(adjustedNits), mappedIPT.yz * chromaScale);

    // Keep negative linear RGB out of the final gamut mapper without clipping hue.
    float3 candidate = IPTPQToAbsolute2020(adjusted);
    if (any(candidate < 0.0))
    {
        float fraction = 0.5;
        [unroll] for (int i = 0; i < 5; i++)
        {
            float3 trial = IPTPQToAbsolute2020(lerp(mappedIPT, adjusted, fraction));
            if (all(trial >= 0.0))
            {
                candidate = trial;
                break;
            }
            fraction *= 0.5;
        }
        if (any(candidate < 0.0))
            return IPTPQToAbsolute2020(mappedIPT);
    }
    return candidate;
}

inline float3 DoviApplyL2(float3 source2020Nits, float3 mapped2020)
{
    if (DOVI_TRIM_LEVEL != 2.0)
        return mapped2020;

    if (DOVI_TRIM_GAIN == 1.0 && DOVI_TRIM_OFFSET == 0.0 &&
        DOVI_TRIM_POWER == 1.0 && DOVI_TRIM_SAT == 0.0 &&
        DOVI_TRIM_CHROMA == 0.0)
        return mapped2020;

    float peak = max(HDR.targetPeakNits, 1e-6);
    float3 rgb = HDR.nativeOutput != 0 ? mapped2020 : mapped2020 * peak;

    // Apply SOP in the normalized target PQ range to preserve shadow detail.
    float y = max(dot(rgb, LUMA_2020), 0.0);
    float minPQ = NitsToPQ(max(HDR.targetMinNits, 0.0));
    float maxPQ = NitsToPQ(peak);
    float rangePQ = max(maxPQ - minPQ, 1e-6);
    float yn = saturate((NitsToPQ(y) - minPQ) / rangePQ);
    float targetYn = pow(saturate(yn * DOVI_TRIM_GAIN + DOVI_TRIM_OFFSET),
                         DOVI_TRIM_POWER);
    float targetY = PQToNits(lerp(minPQ, maxPQ, targetYn));
    if (y > peak * 1e-6)
        rgb *= targetY / y;
    else
        rgb = float3(targetY, targetY, targetY);

    // One perceptual conversion handles both saturation and chroma weight.
    if (DOVI_TRIM_SAT != 0.0 || DOVI_TRIM_CHROMA != 0.0)
    {
        float3 ipt = Absolute2020ToIPTPQ(rgb);
        if (DOVI_TRIM_SAT != 0.0)
            ipt.yz *= max(1.0 + DOVI_TRIM_SAT, 0.0);

        rgb = DOVI_TRIM_CHROMA != 0.0
            ? DoviTrimChromaWeight(source2020Nits, ipt)
            : IPTPQToAbsolute2020(ipt);
    }

    return HDR.nativeOutput != 0 ? rgb : rgb / peak;
}

inline float DoviReshapeComponent(float3 sig, int component)
{
	int b   = DOVI_COMPONENT_BASE + component * DOVI_COMPONENT_VECTORS;
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
	float3 sig      = saturate(c * DOVI_SAMPLE_SCALE);
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

#if defined(dDoviMSDetail)
// Experimental tone-detail approximation. ST 2094-10 defines the P/Q blend,
// not the spatial filter; Dolby's filter is not reproduced here.
inline float DoviDetailSourceLuma(float2 uv)
{
    uv = saturate(uv);
    float3 yuv = float3(Texture1.SampleLevel(Sampler, uv, 0).r,
                        Texture2.SampleLevel(Sampler, uv, 0).rg);
    float3 doviRgb = DoviDecodeYCC(yuv);
    return max(Luma2020(DoviTo2020(PQToLinear(doviRgb, 10000.0))), 0.0);
}

inline float DoviDetailMappedLuma(float sourceY)
{
    // Approximate neighbor luminance through the same PQSpline response.
    if (HDR.nativeOutput != 0 &&
        HDR.sourcePeakNits <= HDR.targetPeakNits &&
        HDR.sourceMinNits >= HDR.targetMinNits)
        return max(sourceY, 0.0);

    float y = clamp(sourceY, HDR.sourceMinNits,
                    max(HDR.sourcePeakNits, HDR.sourceMinNits));
    return clamp(PQToNits(saturate(ToneSpline(NitsToPQ(y)))),
                 max(HDR.targetMinNits, 0.0), HDR.targetPeakNits);
}

inline float3 DoviToneDetail(float3 source2020Nits, float3 mapped2020, float2 uv)
{
    float t = saturate(DOVI_TRIM_MS);
    if (DOVI_TRIM_LEVEL != 2.0 || t <= 0.0 ||
        HDR.targetPeakNits <= max(HDR.targetMinNits, 0.0))
        return mapped2020;

    float mappedY = max(Luma2020(mapped2020), 0.0);
    float displayPeak = max(HDR.targetPeakNits, 1e-6);
    float unit = HDR.nativeOutput != 0 ? displayPeak : 1.0;
    float floorY = max(unit * 1e-7, 1e-9);
    if (mappedY <= floorY)
        return mapped2020;

    uint width, height;
    Texture1.GetDimensions(width, height);
    // Use a small fixed pixel radius.
    float2 texel = 2.0 / max(float2(width, height), 1.0);
    float localSource = 0.25 * (
        DoviDetailSourceLuma(uv - float2(texel.x, 0.0)) +
        DoviDetailSourceLuma(uv + float2(texel.x, 0.0)) +
        DoviDetailSourceLuma(uv - float2(0.0, texel.y)) +
        DoviDetailSourceLuma(uv + float2(0.0, texel.y)));

    float centerSource = max(Luma2020(source2020Nits), 0.0);
    float estimatedCenter = DoviDetailMappedLuma(centerSource);
    float estimatedLocal = DoviDetailMappedLuma(localSource);
    float eps = max(displayPeak * 1e-8, 1e-9);

    // Build the detail candidate Q from a bounded luminance high-pass term.
    float highPassStops = log2((estimatedCenter + eps) / (estimatedLocal + eps));
    highPassStops = clamp(highPassStops, -0.5, 0.5);

    // Weight the effect toward highlights.
    float level = saturate(mappedY / unit);
    float highlight = smoothstep(0.12, 0.70, level);

    float candidateY = mappedY * exp2(highPassStops * highlight);
    float minUnits = HDR.nativeOutput != 0
        ? max(HDR.targetMinNits, 0.0)
        : saturate(HDR.targetMinNits / displayPeak);
    float maxUnits = unit;
    candidateY = clamp(candidateY, minUnits, maxUnits);

    float outputY = lerp(mappedY, candidateY, t);
    return mapped2020 * (outputY / max(mappedY, floorY));
}
#endif
"u8;
}
