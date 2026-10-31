namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_DOVI => @"
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
"u8;
}
