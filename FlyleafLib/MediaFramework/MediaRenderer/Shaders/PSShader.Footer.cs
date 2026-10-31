namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
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
        float y = Luma2020(c);
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
            c = ToneMapIPTPQ2020Absolute(c); // Source exceeds the HDR display | tone-map in IPTPQc4 but stay in absolute nits
        else if (HDR.sourceMinNits < HDR.targetMinNits)
            c = ToneMapLuma2020Absolute(c); // Peak fits; only the lower end needs adaptation. Keep chromaticity scalar

        // Else the complete source range fits | preserve absolute HDR luminance
    }
    else
    {
        if (HDR.sourcePeakNits > HDR.targetPeakNits)
            c = ToneMapIPTPQ2020(c); // Real HDR -> SDR compression. Output is already physical target-relative BT.2020.
        else
            c = ToneMapLuma2020ToTarget(c); // Peak fits, but keep the spline's perceptual brightness adaptation
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
