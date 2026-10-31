namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    static ReadOnlySpan<byte> PS_PQSpline => @"
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
	    ipt.yz = 0.0;

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
"u8;
}
