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

    float k = x > 0.0 ? HDR.splineQa : HDR.splinePa;

    return HDR.splineDstPivot + HDR.splineSlope * x / (1.0 + k * x);
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

// Cone-channel tone mapping with the existing PQ spline
static const float TONE_FILM_STRENGTH = 0.30;

inline float ToneSplineNits(float nits)
{
    float mappedPQ = ToneSpline(NitsToPQ(max(nits, 0.0)));
    return PQToNits(clamp(mappedPQ, NitsToPQ(HDR.targetMinNits), NitsToPQ(HDR.targetPeakNits)));
}

inline float3 ToneMapCone2020Absolute(float3 rgb)
{
    float3 lms      = max(mul(Gamut2020ToLms, rgb), 0.0);
    float peakRGB   = max(max(rgb.r, rgb.g), rgb.b);
    float flare     = max(HDR.targetMinNits, 0.0);

    if (peakRGB <= 1e-7)
        return float3(flare, flare, flare);

    float mappedPeak = ToneSplineNits(peakRGB);
    float gain       = max(mappedPeak - flare, 0.0) / peakRGB;
    float3 preserved = lms * gain;

    float3 film = float3(ToneSplineNits(lms.r),
                         ToneSplineNits(lms.g),
                         ToneSplineNits(lms.b));
    film = max(film - float3(flare, flare, flare), 0.0);

    float kneeNits = PQToNits(HDR.splineSrcPivot);
    float kneeMapped = ToneSplineNits(kneeNits);
    float kneeGain = max(kneeMapped - flare, 1e-9) / max(kneeNits, 1e-9);

    float shoulder = peakRGB > kneeNits
        ? saturate(1.0 - gain / max(kneeGain, 1e-9))
        : 0.0;
    shoulder *= shoulder;

    float3 coneRatio    = saturate(lms / max(max(lms.r, lms.g), max(lms.b, 1e-9)));
    float3 filmWeight   = TONE_FILM_STRENGTH * lerp(coneRatio, float3(1.0, 1.0, 1.0), shoulder);
    float3 mappedLms    = float3(flare, flare, flare) + lerp(preserved, film, filmWeight);

    return mul(GamutLmsTo2020, mappedLms);
}

inline float3 ToneMapCone2020(float3 c)
{   // Absolute linear BT.2020 nits -> target-peak-relative linear BT.2020
    return ToneMapCone2020Absolute(c) / max(HDR.targetPeakNits, 1e-6);
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
