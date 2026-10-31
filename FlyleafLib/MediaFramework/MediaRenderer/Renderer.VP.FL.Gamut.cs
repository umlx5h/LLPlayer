using System.Numerics;

using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11Texture3D = Vortice.Direct3D11.ID3D11Texture3D;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    const int   gamutLutIntensity        = 48;
    const int   gamutLutChroma           = 32;
    const int   gamutLutHue              = 256;
    const float gamutMaxChroma           = 0.5f;
    const float gamutMaxDelta            = 5e-5f;
    const float gamutPerceptualDeadzone  = 0.30f;
    const float gamutPerceptualStrength  = 0.80f;
    const float gamutSoftclipKnee        = 0.70f;

    ID3D11Texture3D          gamutTxt;
    ID3D11ShaderResourceView gamutSrv;
    float                    gamutTargetMinNits  = float.NaN;
    float                    gamutTargetPeakNits = float.NaN;

    void FLGamutUpdateTarget()
    {
        float targetMinNits  = hdrData.TargetMinNits;
        float targetPeakNits = hdrData.TargetPeakNits;

        if (gamutTargetMinNits  == targetMinNits && gamutTxt != null &&
            gamutTargetPeakNits == targetPeakNits)
            return;

        FLGamutDispose();

        ushort[] lut = FLGamutCreateLut(targetMinNits, targetPeakNits);
        
        fixed (ushort* ptr = lut)
        {
            SubresourceData data = new()
            {
                DataPointer = (nint)ptr,
                RowPitch    = gamutLutIntensity * 4 * sizeof(ushort),
                SlicePitch  = gamutLutIntensity * gamutLutChroma * 4 * sizeof(ushort)
            };

            gamutTxt = device.CreateTexture3D(
                Format.R16G16B16A16_UNorm,
                gamutLutIntensity,
                gamutLutChroma,
                gamutLutHue,
                1,
                [data],
                usage: ResourceUsage.Immutable);
        }

        gamutSrv = device.CreateShaderResourceView(gamutTxt);
        context.PSSetShaderResource(5, gamutSrv);

        gamutTargetMinNits  = targetMinNits;
        gamutTargetPeakNits = targetPeakNits;

        float minPQ = NitsToPQ(targetMinNits);
        float maxPQ = NitsToPQ(targetPeakNits);

        hdrData.GamutMinPQ      = minPQ;
        hdrData.GamutInvRangePQ = 1.0f / MathF.Max(maxPQ - minPQ, 1e-6f);
    }

    void FLGamutDispose()
    {
        gamutSrv?.Dispose(); gamutSrv = null;
        gamutTxt?.Dispose(); gamutTxt = null;
    }

    static ushort[] FLGamutCreateLut(float targetMinNits, float targetPeakNits)
    {
        int      texels = gamutLutIntensity * gamutLutChroma * gamutLutHue;
        ushort[] lut    = new ushort[texels * 4];

        float minPQ  = NitsToPQ(targetMinNits);
        float maxPQ  = NitsToPQ(targetPeakNits);
        float minRgb = targetMinNits  / 10000.0f - 1e-6f;
        float maxRgb = targetPeakNits / 10000.0f + 1e-6f;

        Parallel.For(0, gamutLutHue, hueIndex =>
        {
            float hx  = hueIndex / (float)(gamutLutHue - 1);
            float hue = -MathF.PI + hx * MathF.Tau;
            float cos = MathF.Cos(hue);
            float sin = MathF.Sin(hue);

            Vector3 sourcePeak = FLGamutSaturate(hue, true,  minPQ, maxPQ, minRgb, maxRgb);
            Vector3 targetPeak = FLGamutSaturate(hue, false, minPQ, maxPQ, minRgb, maxRgb);
            float   maxChroma  = MathF.Max(sourcePeak.Y, targetPeak.Y);

            for (int chromaIndex = 0; chromaIndex < gamutLutChroma; chromaIndex++)
            {
                float chroma = chromaIndex / (float)(gamutLutChroma - 1) * gamutMaxChroma;

                for (int intensityIndex = 0; intensityIndex < gamutLutIntensity; intensityIndex++)
                {
                    float intensity = minPQ +
                        intensityIndex / (float)(gamutLutIntensity - 1) * (maxPQ - minPQ);

                    Vector3 ipt = new(intensity, chroma * cos, chroma * sin);
                    Vector3 mapped = FLGamutPerceptual(ipt, maxChroma, minRgb, maxRgb);

                    int offset = ((hueIndex * gamutLutChroma + chromaIndex) * gamutLutIntensity + intensityIndex) * 4;

                    lut[offset    ] = FLGamutEncode01(mapped.X);
                    lut[offset + 1] = FLGamutEncodeSigned(mapped.Y);
                    lut[offset + 2] = FLGamutEncodeSigned(mapped.Z);
                    lut[offset + 3] = 0;
                }
            }
        });

        return lut;
    }

    static Vector3 FLGamutPerceptual(Vector3 ipt, float maxChroma, float minRgb, float maxRgb)
    {
        float chroma = MathF.Sqrt(ipt.Y * ipt.Y + ipt.Z * ipt.Z);

        // Perceptual mapping blends sufficiently chromatic colors toward the destination RGB interpretation.
        Vector3 sourceRgb = FLGamutIPTToRGB(ipt, true);
        Vector3 mapped    = FLGamutRGBToIPT(sourceRgb, false);
        float   k         = maxChroma > 1e-6f ? SmoothStep(gamutPerceptualDeadzone, 1.0f, chroma / maxChroma) * gamutPerceptualStrength : 0.0f;

        ipt = Vector3.Lerp(ipt, mapped, k);

        // Final target-RGB soft clip, including the physical display black.
        Vector3 rgb    = FLGamutIPTToRGB(ipt, false);
        float   maxRGB = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));

        rgb.X = MathF.Max(FLGamutSoftClip(rgb.X, maxRGB, maxRgb), minRgb);
        rgb.Y = MathF.Max(FLGamutSoftClip(rgb.Y, maxRGB, maxRgb), minRgb);
        rgb.Z = MathF.Max(FLGamutSoftClip(rgb.Z, maxRGB, maxRgb), minRgb);

        return FLGamutRGBToIPT(rgb, false);
    }

    static Vector3 FLGamutSaturate(float hue, bool source2020, float minPQ, float maxPQ, float minRgb, float maxRgb)
    {
        const float invPhi  = 0.6180339887498948f;
        const float invPhi2 = 0.38196601125010515f;

        Vector3 lo = new(minPQ, 0.0f, hue);
        Vector3 hi = new(maxPQ, 0.0f, hue);
        float   de = hi.X - lo.X;

        Vector3 a = new(lo.X + invPhi2 * de, 0.0f, hue);
        Vector3 b = new(lo.X + invPhi  * de, 0.0f, hue);

        a = FLGamutDesatBounded(a.X, hue, 0.0f, gamutMaxChroma, source2020, minPQ, maxPQ, minRgb, maxRgb);
        b = FLGamutDesatBounded(b.X, hue, 0.0f, gamutMaxChroma, source2020, minPQ, maxPQ, minRgb, maxRgb);

        while (de > gamutMaxDelta)
        {
            de *= invPhi;

            if (a.Y > b.Y)
            {
                hi = b;
                b  = a;
                a.X = lo.X + invPhi2 * de;
                a = FLGamutDesatBounded(a.X, hue, lo.Y - gamutMaxDelta, gamutMaxChroma, source2020, minPQ, maxPQ, minRgb, maxRgb);
            }
            else
            {
                lo = a;
                a  = b;
                b.X = lo.X + invPhi * de;
                b = FLGamutDesatBounded(b.X, hue, hi.Y - gamutMaxDelta, gamutMaxChroma, source2020, minPQ, maxPQ, minRgb, maxRgb);
            }
        }

        return a.Y > b.Y ? a : b;
    }

    static Vector3 FLGamutDesatBounded(
        float intensity,
        float hue,
        float chromaMin,
        float chromaMax,
        bool source2020,
        float minPQ,
        float maxPQ,
        float minRgb,
        float maxRgb)
    {
        if (intensity <= minPQ)
            return new(minPQ, 0.0f, hue);

        if (intensity >= maxPQ)
            return new(maxPQ, 0.0f, hue);

        float maxDI  = intensity * gamutMaxDelta;
        float chroma = (chromaMin + chromaMax) * 0.5f;

        do
        {
            Vector3 ipt = new(intensity, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue));

            if (FLGamutIsInGamut(ipt, source2020, minPQ, maxPQ, minRgb, maxRgb))
                chromaMin = chroma;
            else
                chromaMax = chroma;

            chroma = (chromaMin + chromaMax) * 0.5f;
        }
        while (chromaMax - chromaMin > maxDI);

        return new(intensity, chroma, hue);
    }

    static bool FLGamutIsInGamut(Vector3 ipt, bool source2020, float minPQ, float maxPQ, float minRgb, float maxRgb)
    {
        Vector3 lmsPQ = FLGamutIPTToLMSPQ(ipt);

        if (lmsPQ.X < minPQ || lmsPQ.X > maxPQ ||
            lmsPQ.Y < minPQ || lmsPQ.Y > maxPQ ||
            lmsPQ.Z < minPQ || lmsPQ.Z > maxPQ)
            return false;

        Vector3 rgb = FLGamutLMSToRGB(
            new(
                PQEotf(lmsPQ.X),
                PQEotf(lmsPQ.Y),
                PQEotf(lmsPQ.Z)),
            source2020);

        return rgb.X >= minRgb && rgb.X <= maxRgb &&
               rgb.Y >= minRgb && rgb.Y <= maxRgb &&
               rgb.Z >= minRgb && rgb.Z <= maxRgb;
    }

    static Vector3 FLGamutRGBToIPT(Vector3 rgb, bool source2020)
    {
        Vector3 lms = FLGamutRGBToLMS(rgb, source2020);

        lms.X = PQOetf(lms.X);
        lms.Y = PQOetf(lms.Y);
        lms.Z = PQOetf(lms.Z);

        return new(
            0.4000f * lms.X + 0.4000f * lms.Y + 0.2000f * lms.Z,
            4.4550f * lms.X - 4.8510f * lms.Y + 0.3960f * lms.Z,
            0.8056f * lms.X + 0.3572f * lms.Y - 1.1628f * lms.Z);
    }

    static Vector3 FLGamutIPTToRGB(Vector3 ipt, bool source2020)
    {
        Vector3 lmsPQ = FLGamutIPTToLMSPQ(ipt);
        Vector3 lms = new(
            PQEotf(lmsPQ.X),
            PQEotf(lmsPQ.Y),
            PQEotf(lmsPQ.Z));

        return FLGamutLMSToRGB(lms, source2020);
    }

    static Vector3 FLGamutIPTToLMSPQ(Vector3 c) => new(
            c.X + 0.0975689f * c.Y + 0.2052260f * c.Z,
            c.X - 0.1138760f * c.Y + 0.1332170f * c.Z,
            c.X + 0.0326151f * c.Y - 0.6768870f * c.Z);

    static Vector3 FLGamutRGBToLMS(Vector3 c, bool source2020)
    {
        if (source2020)
            return new(
                0.41203639f * c.X + 0.52391191f * c.Y + 0.06405498f * c.Z,
                0.16666022f * c.X + 0.72039521f * c.Y + 0.11294612f * c.Z,
                0.02411236f * c.X + 0.07547496f * c.Y + 0.90040794f * c.Z);

        return new(
            0.29576408f * c.X + 0.62307245f * c.Y + 0.08116675f * c.Z,
            0.15619198f * c.X + 0.72725164f * c.Y + 0.11655793f * c.Z,
            0.03510228f * c.X + 0.15658995f * c.Y + 0.80830303f * c.Z);
    }

    static Vector3 FLGamutLMSToRGB(Vector3 c, bool source2020)
    {
        if (source2020)
            return new(
                 3.43681483f * c.X - 2.50677380f * c.Y + 0.06995193f * c.Z,
                -0.79105824f * c.X + 1.98360167f * c.Y - 0.19254483f * c.Z,
                -0.02572681f * c.X - 0.09914177f * c.Y + 1.12487414f * c.Z);

        return new(
             6.17353266f * c.X - 5.32089882f * c.Y + 0.14735489f * c.Z,
            -1.32403191f * c.X + 2.56026977f * c.Y - 0.23623862f * c.Z,
            -0.01159839f * c.X - 0.26492145f * c.Y + 1.27652634f * c.Z);
    }

    static float FLGamutSoftClip(float value, float source, float target)
    {
        if (target <= 0.0f)
            return 0.0f;

        float peak = source / target;
        float x    = MathF.Min(value / target, peak);

        if (x <= gamutSoftclipKnee || peak <= 1.0f)
            return value;

        float j = gamutSoftclipKnee;
        float a = -j * j * (peak - 1.0f) / (j * j - 2.0f * j + peak);
        float b = (j * j - 2.0f * j * peak + peak) / MathF.Max(1e-6f, peak - 1.0f);
        float scale = (b * b + 2.0f * b * j + j * j) / (b - a);

        return scale * (x + a) / (x + b) * target;
    }

    static ushort FLGamutEncode01(float value)
        => (ushort)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * ushort.MaxValue);

    static ushort FLGamutEncodeSigned(float value)
        => (ushort)Math.Clamp((int)MathF.Round(value * ushort.MaxValue + 32768.0f), 0, ushort.MaxValue);
}
