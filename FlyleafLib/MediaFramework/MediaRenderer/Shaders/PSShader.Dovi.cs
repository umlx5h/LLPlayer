namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_DOVI => @"
cbuffer DoviConfig : register(b2)
{
	float4 Dovi[201];
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
#define DOVI_L8_LEVEL       Dovi[194].x
#define DOVI_L8_MID         Dovi[194].z
#define DOVI_L8_CLIP        Dovi[194].w
#define DOVI_L8_GAIN        Dovi[195].x
#define DOVI_L8_LIFT        Dovi[195].y
#define DOVI_L8_GAMMA       Dovi[195].z
#define DOVI_L8_CHROMA      Dovi[195].w
#define DOVI_L8_SAT         Dovi[196].x
#define DOVI_L8_MS          Dovi[196].y
#define DOVI_L8_SAT_RYGC    Dovi[197]
#define DOVI_L8_SAT_BM      Dovi[198].xy
#define DOVI_L8_HUE_RY      Dovi[198].zw
#define DOVI_L8_HUE_GCBM    Dovi[199]
#define DOVI_L5_RECT         Dovi[200]
inline bool DoviL5Active(float2 uv)
{
    if (DOVI_L5_RECT.z == 0.0)
        return true;
    return all(uv >= DOVI_L5_RECT.xy) && all(uv < DOVI_L5_RECT.zw);
}
#define DOVI_FEL_ACTIVE      Dovi[190].x
#define DOVI_EL_CODE_SCALE   Dovi[190].y
#define DOVI_EL_UV_SCALE     Dovi[190].zw

// Chroma Weight is applied in IPTPQ after luminance and saturation trims.
// The source retains absolute BT.2020 nits; final gamut mapping remains separate.
inline float3 DoviTrimChromaWeight(float3 source2020Nits, float3 mappedIPT, float chromaWeight)
{
    float3 sourceIPT = Absolute2020ToIPTPQ(source2020Nits);
    float saturation = length(sourceIPT.yz);
    if (saturation < 1e-5)
        return IPTPQToAbsolute2020(mappedIPT);

    float intensityScale = max(0.0, 1.0 + (4.0 * chromaWeight) * saturation);
    float chromaScale    = max(0.0, 1.0 + (3.0 * chromaWeight) * saturation);

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
            ? DoviTrimChromaWeight(source2020Nits, ipt, DOVI_TRIM_CHROMA)
            : IPTPQToAbsolute2020(ipt);
    }

    return HDR.nativeOutput != 0 ? rgb : rgb / peak;
}

// Approximate CM4 primary trims; Dolby's reference tone curve is proprietary.
inline float3 DoviApplyL8(float3 source2020Nits, float3 mapped2020)
{
    if (DOVI_L8_LEVEL != 8.0)
        return mapped2020;

    if (DOVI_L8_GAIN == 1.0 && DOVI_L8_LIFT == 0.0 &&
        DOVI_L8_GAMMA == 1.0 && DOVI_L8_SAT == 0.0 &&
        DOVI_L8_CHROMA == 0.0 && DOVI_L8_MID == 0.0 &&
        DOVI_L8_CLIP == 0.0)
        return mapped2020;

    float peak = max(HDR.targetPeakNits, 1e-6);
    float3 rgb = HDR.nativeOutput != 0 ? mapped2020 : mapped2020 * peak;
    float y = max(Luma2020(rgb), 0.0);
    float minPQ = NitsToPQ(max(HDR.targetMinNits, 0.0));
    float rangePQ = max(NitsToPQ(peak) - minPQ, 1e-6);
    float yn = saturate((NitsToPQ(y) - minPQ) / rangePQ);

    // CM4 lift, gamma and gain are kept separate from the CM2.9 SOP curve.
    yn = pow(saturate(yn * DOVI_L8_GAIN + DOVI_L8_LIFT), max(DOVI_L8_GAMMA, 1e-4));

    // Symmetric midtone contrast around 50%; keep endpoints fixed.
    yn = saturate(yn + (DOVI_L8_MID * 0.7) *
                  (4.0 * yn * (1.0 - yn) * (yn - 0.5)));

    // Positive clip trim progressively limits the highlight ceiling.
    float clip = saturate(DOVI_L8_CLIP * 2.0);
    if (clip > 0.0)
    {
        float ceiling = 1.0 - 0.25 * clip;
        float knee = max(ceiling - 0.12, 0.0);
        if (yn > knee)
            yn = knee + (ceiling - knee) *
                 (1.0 - exp(-(yn - knee) / max(ceiling - knee, 1e-6)));
    }

    float targetY = PQToNits(minPQ + yn * rangePQ);
    if (y > peak * 1e-6)
        rgb *= targetY / y;
    else
        rgb = float3(targetY, targetY, targetY);

    if (DOVI_L8_SAT != 0.0 || DOVI_L8_CHROMA != 0.0)
    {
        float3 ipt = Absolute2020ToIPTPQ(rgb);
        ipt.yz *= max(1.0 + DOVI_L8_SAT, 0.0);
        rgb = DOVI_L8_CHROMA != 0.0
            ? DoviTrimChromaWeight(source2020Nits, ipt, DOVI_L8_CHROMA)
            : IPTPQToAbsolute2020(ipt);
    }

    return HDR.nativeOutput != 0 ? rgb : rgb / peak;
}

// Hue anchors for BT.2020 R/Y/G/C/B/M in IPTPQ at 100 nits.
inline float2 DoviL8SecondaryAt(float h)
{
    float2 r = float2(DOVI_L8_SAT_RYGC.x, DOVI_L8_HUE_RY.x);
    float2 y = float2(DOVI_L8_SAT_RYGC.y, DOVI_L8_HUE_RY.y);
    float2 g = float2(DOVI_L8_SAT_RYGC.z, DOVI_L8_HUE_GCBM.x);
    float2 c = float2(DOVI_L8_SAT_RYGC.w, DOVI_L8_HUE_GCBM.y);
    float2 b = float2(DOVI_L8_SAT_BM.x,   DOVI_L8_HUE_GCBM.z);
    float2 m = float2(DOVI_L8_SAT_BM.y,   DOVI_L8_HUE_GCBM.w);

    h = h < 0.64920 ? h + 6.28318531 : h;
    if (h < 1.79875) return lerp(r, y, smoothstep(0.64920, 1.79875, h));
    if (h < 2.38138) return lerp(y, g, smoothstep(1.79875, 2.38138, h));
    if (h < 3.44598) return lerp(g, c, smoothstep(2.38138, 3.44598, h));
    if (h < 4.27120) return lerp(c, b, smoothstep(3.44598, 4.27120, h));
    if (h < 5.94541) return lerp(b, m, smoothstep(4.27120, 5.94541, h));
    return lerp(m, r, smoothstep(5.94541, 6.93239, h));
}

// Approximate CM4 SDR secondary trims in IPTPQ before gamut mapping.
inline float3 DoviApplyL8Secondary(float3 mapped2020)
{
    if (HDR.nativeOutput != 0 || DOVI_L8_LEVEL != 8.0 ||
        (all(Dovi[197] == 0.0) && all(Dovi[198] == 0.0) && all(Dovi[199] == 0.0)))
        return mapped2020;

    float peak = max(HDR.targetPeakNits, 1e-6);
    float3 original = Absolute2020ToIPTPQ(mapped2020 * peak);
    float chroma = length(original.yz);
    if (chroma < 1e-5)
        return mapped2020;

    float2 trim = DoviL8SecondaryAt(atan2(original.z, original.y));
    trim *= smoothstep(0.001, 0.025, chroma);
    if (all(trim == 0.0))
        return mapped2020;

    float angle = trim.y * 0.34906585;
    float sn = sin(angle), cs = cos(angle);
    float scale = max(0.0, 1.0 + trim.x * 0.75);
    float3 adjusted = original;
    adjusted.yz = scale * float2(original.y * cs - original.z * sn,
                                 original.y * sn + original.z * cs);

    float3 rgb = IPTPQToAbsolute2020(adjusted);
    if (any(rgb < 0.0))
    {
        float fraction = 0.5;
        [unroll] for (int i = 0; i < 6; i++)
        {
            rgb = IPTPQToAbsolute2020(lerp(original, adjusted, fraction));
            if (all(rgb >= 0.0))
                break;
            fraction *= 0.5;
        }
        if (any(rgb < 0.0))
            return mapped2020;
    }
    return rgb / peak;
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

#if defined(dDoviFEL)
// EL is a coded YUV residual; round the sampled codes before NLQ.
inline float3 DoviComposeFEL(float3 reshaped, float2 uv)
{
    if (DOVI_FEL_ACTIVE == 0.0)
        return reshaped;

    float2 elUV = saturate(uv * DOVI_EL_UV_SCALE);
    float3 el = float3(
        Texture3.SampleLevel(Sampler, elUV, 0).r,
        Texture4.SampleLevel(Sampler, elUV, 0).rg);
    float3 rr = round(el * DOVI_EL_CODE_SCALE);

    float3 residual;
    [unroll] for (int c = 0; c < 3; c++)
    {
        float4 nlq = Dovi[191 + c];
        float centered = round(rr[c] - nlq.x);
        float r = sign(centered) *
                  ((abs(centered) - 0.5) * nlq.y + nlq.z);
        residual[c] = clamp(r, -nlq.w, nlq.w);
    }

    return saturate(reshaped + residual);
}
#endif

inline float3 DoviDecodeYCC(float3 c, float2 uv)
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

#if defined(dDoviFEL)
    reshaped = DoviComposeFEL(reshaped, uv);
#endif

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
    float3 doviRgb = DoviDecodeYCC(yuv, uv);
    return max(Luma2020(DoviTo2020(PQToLinear(doviRgb, 10000.0))), 0.0);
}

inline float DoviDetailMappedLuma(float sourceY)
{
    // Approximate neighbor luminance through the same PQSpline response.
    if (HDR.nativeOutput != 0 &&
        HDR.sourcePeakNits <= HDR.targetPeakNits &&
        HDR.sourceMinNits >= HDR.targetMinNits)
        return max(sourceY, 0.0);

    return ToneSplineNits(sourceY);
}

inline float3 DoviToneDetail(float3 source2020Nits, float3 mapped2020, float2 uv)
{
    float t = DOVI_L8_LEVEL == 8.0 ? DOVI_L8_MS :
              (DOVI_TRIM_LEVEL == 2.0 ? DOVI_TRIM_MS : 0.0);
    if (t == 0.0 || HDR.targetPeakNits <= max(HDR.targetMinNits, 0.0))
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

    float candidateY = mappedY * exp2(highPassStops * highlight * sign(t));
    float minUnits = HDR.nativeOutput != 0
        ? max(HDR.targetMinNits, 0.0)
        : saturate(HDR.targetMinNits / displayPeak);
    float maxUnits = unit;
    candidateY = clamp(candidateY, minUnits, maxUnits);

    float outputY = lerp(mappedY, candidateY, saturate(abs(t)));
    return mapped2020 * (outputY / max(mappedY, floorY));
}
#endif
"u8;
}
