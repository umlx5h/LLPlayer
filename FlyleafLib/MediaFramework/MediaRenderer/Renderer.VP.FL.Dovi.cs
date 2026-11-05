using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D11;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    /// <summary>
    /// Whether to use Dolby Vision reconstruction or the compatible base layer
    /// </summary>
    public bool DoviPreferBaseLayer;

    /// <summary>
    /// Whether to use Dolby L1 metadata or Flyleaf's histogram for tone mapping
    /// </summary>
    public bool DoviUseHDRDetect;
    bool? doviIsCM4;
    float doviL1TargetMin, doviL1TargetPeak;

    const int doviVectors             = 201; // Reconstruction, L2, FEL, L8, L5
    const int doviTrimInfo            = 187;
    const int doviTrimPrimary         = 188;
    const int doviTrimSecondary       = 189;
    const int doviFelInfo             = 190;
    const int doviFelNLQ              = 191;
    const int doviL8Info              = 194;
    const int doviL8Primary           = 195;
    const int doviL8Secondary         = 196;
    const int doviL8Saturation0       = 197;
    const int doviL8Saturation1Hue0   = 198;
    const int doviL8Hue1              = 199;
    const int doviL5Bounds           = 200;
    const int doviComponentVectors    = 59;
    const int doviComponentBase       = 10;
    const int doviMmrVectors          = 48;
    const float doviPivotSentinel     = 1e9f;

    // Dovi[0]      : sampleScale, identityReshape, 0, 0
    // Dovi[1..3]   : affine YCC -> nonlinear RGB rows
    // Dovi[4..6]   : linear DOVI RGB -> BT.2020 rows, BT.2020 luma coefficients in .w
    // Dovi[7..9]   : linear DOVI RGB -> BT.709 rows
    // Dovi[10..186]: three reshape components, 59 float4s each
    // Dovi[187]    : L2 present (0/2), authored reference peak, 0, 0
    // Dovi[188]    : slope, offset, power, chroma weight
    // Dovi[189]    : saturation gain, tone detail factor, 0, 0
    // Dovi[190]    : FEL active, EL code scale, EL UV scale X, EL UV scale Y (render-time)
    // Dovi[191..193]: per-YUV-channel NLQ offset, slope, threshold, hdr_in_max
    // Dovi[194..196]: L8 info and primary trims
    // Dovi[197..199]: L8 secondary saturation and hue trims
    // Dovi[200]     : L5 active rectangle, normalized texture coordinates
    static BufferDescription doviDesc = new()
    {
        Usage          = ResourceUsage.Default,
        BindFlags      = BindFlags.ConstantBuffer,
        CPUAccessFlags = CpuAccessFlags.None,
        ByteWidth      = doviVectors * 16
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct DoviBufferType
    {
        public fixed float Data[doviVectors * 4];
    }

    internal readonly record struct DoviTrimSnapshot(
        int Level, float TargetNits,
        float Slope, float Offset, float Power,
        float ChromaWeight, float SaturationGain, float MsWeight);

    internal enum DoviTargetGamut : byte { Unknown, BT709, P3, BT2020 }

    internal readonly record struct DoviL8Target(
        byte DisplayIndex, float PeakNits, float MinNits,
        DoviTargetGamut Gamut, bool IsPQ, byte PrimaryIndex);

    internal readonly record struct DoviL8Vector(
        byte V0, byte V1, byte V2, byte V3, byte V4, byte V5);

    internal readonly record struct DoviL8Snapshot(
        int Level, DoviL8Target Target,
        float Slope, float Offset, float Power,
        float ChromaWeight, float SaturationGain,
        ushort MsWeight, ushort MidContrast, ushort ClipTrim,
        DoviL8Vector Saturation, DoviL8Vector Hue,
        bool HasMidContrast, bool HasClipTrim, bool HasSaturation, bool HasHue);

    internal sealed class DoviFrameData
    {
        public DoviBufferType Buffer;
        internal DoviTrimSnapshot Trim;
        internal DoviTrimSnapshot[] L2Targets;
        internal DoviL8Snapshot L8;
        internal DoviL8Snapshot[] L8Targets;
        internal DoviTargetGamut SourceGamut;
        internal Vector4 L5Bounds;
        internal bool? IsCM4;
        internal bool HasFEL;
        internal byte ELBitDepth;
    }

    DoviFrameData doviPreparedData; // Prepare-side cache for repeated RPUs.
    DoviTargetGamut doviCachedSourceGamut;
    readonly Dictionary<byte, DoviL8Target> doviCachedL10 = [];
    (float Min, float Avg, float Max)? doviL1Nits;

    DoviFrameData doviAppliedData;  // Render-side cache
    DoviTrimSnapshot doviAppliedTrim;
    DoviL8Snapshot doviAppliedL8;
    bool doviAppliedHDRDetect;
    bool doviAppliedEmpty;
    Vector4 doviAppliedFELInfo;
    Vector4 doviAppliedL5Bounds;
    Vector4 felInfo;

    // FFmpeg reattaches fresh side data every frame; compare actual metadata.
    bool                    doviSourceCached;
    byte                    doviCachedBlBitDepth;
    byte                    doviCachedCoefLog2Denom;
    byte                    doviCachedElBitDepth;
    bool                    doviCachedFEL;
    AVDOVINLQParams         doviCachedNLQ0, doviCachedNLQ1, doviCachedNLQ2;
    AVDOVIReshapingCurve    doviCachedCurve0, doviCachedCurve1, doviCachedCurve2;
    AVDOVIColorMetadata     doviCachedColor;

    static ReadOnlySpan<float> DoviHpeLmsTo2020 =>
    [
         3.06441879f, -2.16597676f,  0.10155818f,
        -0.65612108f,  1.78554118f, -0.12943749f,
         0.01736321f, -0.04725154f,  1.03004253f
    ];

    static ReadOnlySpan<float> Dovi2020To709 =>
    [
         1.6605f, -0.5876f, -0.0728f,
        -0.1246f,  1.1329f, -0.0083f,
        -0.0182f, -0.1006f,  1.1187f
    ];

    static ReadOnlySpan<float> DoviLuma2020 => [0.2627f, 0.6780f, 0.0593f];

    bool FLDoviSupported()
    {
        if (scfg == null || !scfg.DoviRpuPresent || !scfg.DoviBlPresent || (DoviPreferBaseLayer && scfg.HDRFormat != HDRFormat.None))
            return false;
        
        return scfg.DoviProfile switch
        {
            // Unknown profile; DOVI metadata confirmed on decoded frame
            -1 => true,

            // Profile 5 has no standards-compatible BL and therefore requires
            // Dolby reconstruction for correct playback. Compatibility ID must be 0.
            5 => scfg.DoviCompatibility == 0,

            // Profile 7 is HDR10/UHD-BD compatible. Older configuration records
            // may omit the compatibility nibble (0), while current ones use 6.
            7 => scfg.DoviCompatibility is 0 or 6,

            // Profile 8 is single-layer and the compatibility ID defines the BL:
            // 0 none/proprietary, 1 HDR10, 2 SDR, 4 HLG, 6 UHD-BD/HDR10.
            8 => scfg.DoviCompatibility is 0 or 1 or 2 or 4 or 6,

            // Profile 10 is single-layer 10-bit AV1. Public Dolby variants are
            // 10.0 (native/proprietary), 10.1 (HDR10) and 10.4 (HLG). FFmpeg
            // can also signal compatibility 2 for a BT.709/SDR-compatible BL,
            // and the parsed RPU uses the same AVDOVIMetadata reconstruction.
            10 => scfg.DoviCompatibility is 0 or 1 or 2 or 4,

            _ => false
        };
    }

    void FLDoviReset(AVFrame* frame)
    {
        doviL1Nits          = null;
        doviL1TargetMin     = float.NaN;
        doviL1TargetPeak    = float.NaN;
        doviPreparedData    = null;
        doviCachedSourceGamut = DoviTargetGamut.Unknown;
        doviCachedL10.Clear();
        doviAppliedData     = null;
        doviAppliedTrim     = default;
        doviAppliedL8       = default;
        doviAppliedHDRDetect = false;
        doviAppliedEmpty    = false;
        doviAppliedFELInfo  = default;
        doviAppliedL5Bounds = default;
        doviSourceCached    = false;
        doviCachedColor     = default;
        doviCachedCurve0    = default;
        doviCachedCurve1    = default;
        doviCachedCurve2    = default;
        doviCachedNLQ0      = default;
        doviCachedNLQ1      = default;
        doviCachedNLQ2      = default;
        doviCachedFEL       = false;
        doviIsCM4           = FLDoviIsCM4(frame);
    }

    static bool? FLDoviIsCM4(AVFrame* frame)
    {
        if (frame == null)
            return null;

        var side = av_frame_side_data_get(frame->side_data, frame->nb_side_data, AVFrameSideDataType.DOVIMetadata);
        return side == null || side->data == null ? null : FLDoviIsCM4((AVDOVIMetadata*)side->data);
    }

    static bool? FLDoviIsCM4(AVDOVIMetadata* dovi)
    {
        bool hasCM4 = false, hasVersion = false;
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level == 254)
            {
                hasVersion = true;
                var version = dm->union0.l254;
                if (version.dm_mode == 0 && version.dm_version_index <= 2)
                    return true;
            }
            else if (dm->level is 3 or 8)
                hasCM4 = true;
        }

        if (hasCM4)
            return true;
        return hasVersion ? null : false;
    }

    internal static bool FLIsDoviFEL(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping)
    {
        if (header == null || mapping == null || header->disable_residual_flag != 0 || mapping->nlq_method_idc != AVDOVINLQMethod.LinearDz || header->coef_log2_denom >= 64)
            return false;

        ulong one = 1UL << header->coef_log2_denom;

        for (int c = 0; c < 3; c++)
        {
            AVDOVINLQParams nlq = mapping->nlq[c];

            if (nlq.nlq_offset != 0 ||
                nlq.vdr_in_max != one ||
                nlq.linear_deadzone_slope != 0 ||
                nlq.linear_deadzone_threshold != 0)
                return true;
        }

        return false;
    }
    
    (DoviFrameData, (float Min, float Avg, float Max)?) FLDoviPrepare(AVFrame* frame)
    {
        var side = av_frame_side_data_get(frame->side_data, frame->nb_side_data, AVFrameSideDataType.DOVIMetadata);

        if (side == null || side->data == null)
            return (null, null);

        var dovi    = (AVDOVIMetadata*)side->data;
        var header  = (AVDOVIRpuDataHeader*)((byte*)dovi + dovi->header_offset);
        var mapping = (AVDOVIDataMapping*)  ((byte*)dovi + dovi->mapping_offset);
        var color   = (AVDOVIColorMetadata*)((byte*)dovi + dovi->color_offset);

        // Guard malformed coefficient ranges
        if (header->coef_log2_denom < 13 || header->coef_log2_denom > 32)
            return (null, null);

        // CM version is stream-wide; L2 on a CM4 frame is only compatibility metadata.
        bool? detected = FLDoviIsCM4(dovi);
        if (detected == true && doviIsCM4 != true)
        {
            doviIsCM4 = true;
            doviL1Nits = null;
            doviCachedSourceGamut = DoviTargetGamut.Unknown;
            doviCachedL10.Clear();
        }
        else if (doviIsCM4 == null && detected == false)
            doviIsCM4 = false;

        bool? cm4 = doviIsCM4;
        bool hasFEL = scfg.DoviProfile == 7 && header->el_bit_depth is >= 8 and <= 16 &&
                      FLIsDoviFEL(header, mapping);

        if (cm4 == true)
            FLDoviUpdateL10(dovi);
        var l2Targets = cm4 == false ? FLDoviReadL2(dovi) : null;
        var trim      = FLDoviSelectTrim(l2Targets);
        var l8Targets = cm4 == true ? FLDoviReadL8(dovi) : null;
        var l8        = FLDoviSelectL8(l8Targets);
        var sourceGamut = cm4 == true ? FLDoviSourceGamut(dovi) : DoviTargetGamut.Unknown;
        var l5Bounds = FLDoviReadL5(dovi);

        if (doviPreparedData != null && DoviSourceEquals(header, mapping, color, hasFEL))
        {
            if (doviPreparedData.Trim != trim || doviPreparedData.L8 != l8 ||
                doviPreparedData.IsCM4 != cm4 || doviPreparedData.SourceGamut != sourceGamut ||
                doviPreparedData.L5Bounds != l5Bounds ||
                !(doviPreparedData.L2Targets ?? []).AsSpan().SequenceEqual((l2Targets ?? []).AsSpan()) ||
                !(doviPreparedData.L8Targets ?? []).AsSpan().SequenceEqual((l8Targets ?? []).AsSpan()))
            {
                // Keep queued frames immutable.
                DoviBufferType nextBuffer = doviPreparedData.Buffer;
                DoviWriteTrim(ref nextBuffer, trim);
                DoviWriteL8(ref nextBuffer, l8);
                doviPreparedData = new()
                {
                    Buffer = nextBuffer, Trim = trim, L2Targets = l2Targets,
                    L8 = l8, L8Targets = l8Targets,
                    SourceGamut = sourceGamut, L5Bounds = l5Bounds, IsCM4 = cm4,
                    HasFEL = doviPreparedData.HasFEL,
                    ELBitDepth = doviPreparedData.ELBitDepth
                };
            }
            return (doviPreparedData, FLDoviL1Nits(dovi));
        }
        
        // Guard malformed BL depth
        if (header->bl_bit_depth < 8 || header->bl_bit_depth > 16)
            return (null, null);

        int sampleBits = scfg.PixelComp0Depth > 8 ? 16 : 8;
        if (header->bl_bit_depth > sampleBits)
            return (null, null);

        uint sampleMax = (1u << sampleBits) - 1u;
        uint blMax     = (1u << header->bl_bit_depth) - 1u;
        int shift      = sampleBits - header->bl_bit_depth;

        float sampleScale = sampleMax / (blMax * (float)(1u << shift));
        float coefScale   = MathF.ScaleB(1.0f, -header->coef_log2_denom);
        float pivotScale  = 1.0f / blMax;

        if (!float.IsFinite(sampleScale) || !float.IsFinite(coefScale))
            return (null, null);

        DoviBufferType buffer = default;

        // All temporary matrix work stays on the stack. RPU metadata may be present
        // on every frame, so avoid creating managed 3x3 arrays in this hot path.
        Span<float> ycc        = stackalloc float[9];
        Span<float> offset     = stackalloc float[3];
        Span<float> rgbToLms   = stackalloc float[9];
        Span<float> doviTo2020 = stackalloc float[9];
        Span<float> doviTo709  = stackalloc float[9];
        Span<float> luma       = stackalloc float[3];

        for (int r = 0; r < 3; r++)
        {
            if (!DoviTryFloat(color->ycc_to_rgb_offset[r], out offset[r]))
                return (null, null);

            for (int c = 0; c < 3; c++)
            {
                if (!DoviTryFloat(color->ycc_to_rgb_matrix[r * 3 + c], out ycc[r * 3 + c]) ||
                    !DoviTryFloat(color->rgb_to_lms_matrix[r * 3 + c], out rgbToLms[r * 3 + c]))
                    return (null, null);
            }
        }

        // RPU custom YCC -> nonlinear RGB. Fold the neutral offsets into row .w.
        for (int r = 0; r < 3; r++)
        {
            int i = r * 3;
            float bias = -(ycc[i] * offset[0] + ycc[i + 1] * offset[1] + ycc[i + 2] * offset[2]);
            DoviSet(ref buffer, 1 + r, new(ycc[i], ycc[i + 1], ycc[i + 2], bias));
        }

        // Fold RGB->LMS into BT.2020 first, then derive the BT.709 output matrix.
        // Keep both: BT.2020 is the source colour volume for gamut mapping, while
        // BT.709 is the renderer/output representation after tone mapping.
        DoviMul3x3(DoviHpeLmsTo2020, rgbToLms, doviTo2020);
        DoviMul3x3(Dovi2020To709, doviTo2020, doviTo709);
        DoviMulRow3x3(DoviLuma2020, doviTo2020, luma);

        for (int r = 0; r < 3; r++)
        {
            int i = r * 3;

            Vector4 row2020 = new(doviTo2020[i], doviTo2020[i + 1], doviTo2020[i + 2], luma[r]);
            Vector4 row709  = new(doviTo709[i],  doviTo709[i + 1],  doviTo709[i + 2],  0);

            if (!DoviFinite(row2020) || !DoviFinite(row709))
                return (null, null);

            DoviSet(ref buffer, 4 + r, row2020);
            DoviSet(ref buffer, 7 + r, row709);
        }

        // Seven possible internal pivots. Allocate once for the whole method, not
        // inside the component loop (avoids CA2014 and repeated stack growth).
        Span<float> pivots = stackalloc float[7];
        bool identityReshape = true;

        for (int component = 0; component < 3; component++)
        {
            ref var curve = ref mapping->curves[component];
            int numPivots = curve.num_pivots;

            // FFmpeg contract: [2, 9], strictly sorted ascending.
            if (numPivots < 2 || numPivots > 9)
                return (null, null);

            ushort previous = curve.pivots[0];
            if (previous > blMax)
                return (null, null);

            for (int i = 1; i < numPivots; i++)
            {
                ushort current = curve.pivots[i];
                if (current < previous || current > blMax)
                    return (null, null);
                previous = current;
            }

            int componentBase = doviComponentBase + component * doviComponentVectors;
            float lo = curve.pivots[0] * pivotScale;
            float hi = curve.pivots[numPivots - 1] * pivotScale;

            DoviSet(ref buffer, componentBase, new(lo, hi, numPivots, 0));

            pivots.Fill(doviPivotSentinel);
            for (int i = 1; i < numPivots - 1; i++)
                pivots[i - 1] = curve.pivots[i] * pivotScale;

            DoviSet(ref buffer, componentBase + 1,
                new(pivots[0], pivots[1], pivots[2], pivots[3]));
            DoviSet(ref buffer, componentBase + 2,
                new(pivots[4], pivots[5], pivots[6], doviPivotSentinel));

            // Common Profile 7 mappings are exact identity transforms. Signal
            // this to HLSL so it can bypass all pivot/polynomial work per pixel.
            identityReshape &=
                numPivots == 2 &&
                curve.pivots[0] == 0 &&
                curve.pivots[1] == blMax &&
                curve.mapping_idc[0] == AVDOVIMappingMethod.Polynomial &&
                curve.poly_order[0] == 1 &&
                curve.poly_coef[0][0] == 0 &&
                curve.poly_coef[0][1] == (1L << header->coef_log2_denom);

            int mmrIndex = 0;
            int pieces   = numPivots - 1;

            for (int p = 0; p < pieces; p++)
            {
                switch (curve.mapping_idc[p])
                {
                    case AVDOVIMappingMethod.Polynomial:
                    {
                        int order = curve.poly_order[p];
                        if (order < 1 || order > 2)
                            return (null, null);

                        float c0 = curve.poly_coef[p][0] * coefScale;
                        float c1 = curve.poly_coef[p][1] * coefScale;
                        float c2 = order == 2 ? curve.poly_coef[p][2] * coefScale : 0;

                        if (!float.IsFinite(c0) || !float.IsFinite(c1) || !float.IsFinite(c2))
                            return (null, null);

                        DoviSet(ref buffer, componentBase + 3 + p, new(c0, c1, c2, 0));
                        break;
                    }

                    case AVDOVIMappingMethod.Mmr:
                    {
                        int order = curve.mmr_order[p];
                        if (order < 1 || order > 3)
                            return (null, null);

                        int vectors = order * 2;
                        if (mmrIndex + vectors > doviMmrVectors)
                            return (null, null);

                        float constant = curve.mmr_constant[p] * coefScale;
                        if (!float.IsFinite(constant))
                            return (null, null);

                        DoviSet(ref buffer, componentBase + 3 + p,
                            new(constant, mmrIndex, 0, order));

                        for (int o = 0; o < order; o++)
                        {
                            int mmrBase = componentBase + 11 + mmrIndex;

                            Vector4 a = new(
                                curve.mmr_coef[p][o][0] * coefScale,
                                curve.mmr_coef[p][o][1] * coefScale,
                                curve.mmr_coef[p][o][2] * coefScale,
                                0);

                            Vector4 b = new(
                                curve.mmr_coef[p][o][3] * coefScale,
                                curve.mmr_coef[p][o][4] * coefScale,
                                curve.mmr_coef[p][o][5] * coefScale,
                                curve.mmr_coef[p][o][6] * coefScale);

                            if (!DoviFinite(a) || !DoviFinite(b))
                                return (null, null);

                            DoviSet(ref buffer, mmrBase,     a);
                            DoviSet(ref buffer, mmrBase + 1, b);

                            mmrIndex += 2;
                        }
                        break;
                    }

                    default:
                        return (null, null);
                }
            }
        }

        DoviSet(ref buffer, 0, new(sampleScale, identityReshape ? 1.0f : 0.0f, 0, 0));
        DoviWriteTrim(ref buffer, trim);
        DoviWriteL8(ref buffer, l8);

        // NLQ coefficients stay with the RPU; EL surface binding is render-time.
        if (hasFEL)
        {
            for (int c = 0; c < 3; c++)
            {
                var nlq = mapping->nlq[c];
                DoviSet(ref buffer, doviFelNLQ + c, new(
                    nlq.nlq_offset,
                    (float)(nlq.linear_deadzone_slope * coefScale),
                    (float)(nlq.linear_deadzone_threshold * coefScale),
                    (float)(nlq.vdr_in_max * coefScale)));
            }
        }

        DoviCacheSource(header, mapping, color, hasFEL);
        doviPreparedData = new()
        {
            Buffer = buffer, Trim = trim, L2Targets = l2Targets,
            L8 = l8, L8Targets = l8Targets,
            SourceGamut = sourceGamut, L5Bounds = l5Bounds, IsCM4 = cm4,
            HasFEL = hasFEL, ELBitDepth = header->el_bit_depth
        };
        return (doviPreparedData, FLDoviL1Nits(dovi));
    }

    Vector4 FLDoviReadL5(AVDOVIMetadata* dovi)
    {
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level != 5)
                continue;

            var l5 = dm->union0.l5;
            float width = scfg.txtWidth - scfg.Crop.Width;
            float height = scfg.txtHeight - scfg.Crop.Height;
            float left = l5.left_offset, right = l5.right_offset;
            float top = l5.top_offset, bottom = l5.bottom_offset;
            if ((left == 0 && right == 0 && top == 0 && bottom == 0) ||
                width <= 0 || height <= 0 || left + right >= width || top + bottom >= height)
                return default;

            return new(
                (scfg.Crop.Left + left) / scfg.txtWidth,
                (scfg.Crop.Top + top) / scfg.txtHeight,
                (scfg.txtWidth - scfg.Crop.Right - right) / scfg.txtWidth,
                (scfg.txtHeight - scfg.Crop.Bottom - bottom) / scfg.txtHeight);
        }

        return default;
    }

    void FLDoviSetL5(Vector4 bounds)
    {
        if (doviAppliedL5Bounds == bounds)
            return;

        doviAppliedL5Bounds = bounds;
        FLHDRDetectReset();
    }

    (float Min, float Avg, float Max)? FLDoviL1Nits(AVDOVIMetadata* dovi)
    {
        AVDOVIDmData* level1 = null;
        AVDOVIDmData* level3 = null;

        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level == 1) level1 = dm;
            if (dm->level == 3) level3 = dm;
        }

        if (level1 == null)
            return null;

        var l1 = level1->union0.l1;
        if (l1.min_pq > 4095 || l1.avg_pq > 4095 || l1.max_pq > 4095)
            return null;

        if (doviIsCM4 != true || level3 == null)
            return (PQToNitsDovi(l1.min_pq), PQToNitsDovi(l1.avg_pq), PQToNitsDovi(l1.max_pq));

        var l3 = level3->union0.l3;
        if (l3.min_pq_offset > 4095 || l3.avg_pq_offset > 4095 || l3.max_pq_offset > 4095)
            return null;

        float minPQ = Math.Clamp(l1.min_pq / 4095f + (l3.min_pq_offset - 2048) / 2048f, 0f, 1f);
        float avgPQ = Math.Clamp(l1.avg_pq / 4095f + (l3.avg_pq_offset - 2048) / 2048f, 0f, 1f);
        float maxPQ = Math.Clamp(l1.max_pq / 4095f + (l3.max_pq_offset - 2048) / 2048f, 0f, 1f);

        if (minPQ > maxPQ)
            return null;

        return (PQToNits(minPQ), PQToNits(Math.Clamp(avgPQ, minPQ, maxPQ)), PQToNits(maxPQ));
    }

    static float DoviTrimSigned(ushort code) => code / 4096f - 0.5f;
    static float DoviTrimUnit(ushort code)   => code / 4096f + 0.5f;

    static float DoviTrimLerp(float a, float b, float t) => a + (b - a) * t;

    static DoviTrimSnapshot DoviInterpolateTrim(DoviTrimSnapshot low, DoviTrimSnapshot high, float targetNits, float t)
        => new(
            2, targetNits,
            DoviTrimLerp(low.Slope, high.Slope, t),
            DoviTrimLerp(low.Offset, high.Offset, t),
            DoviTrimLerp(low.Power, high.Power, t),
            DoviTrimLerp(low.ChromaWeight, high.ChromaWeight, t),
            DoviTrimLerp(low.SaturationGain, high.SaturationGain, t),
            DoviTrimLerp(low.MsWeight, high.MsWeight, t));

    static DoviTrimSnapshot[] FLDoviReadL2(AVDOVIMetadata* dovi)
    {
        List<DoviTrimSnapshot> result = [];
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level != 2)
                continue;

            var l2 = dm->union0.l2;
            if (l2.target_max_pq is 0 or > 4095)
                continue;

            float referenceNits = PQToNitsDovi(l2.target_max_pq);
            int ms = l2.ms_weight;
            float msFactor = MathF.Abs(referenceNits - 100f) <= 2f || ms is < 0 or > 4095
                ? 0f : (ms - 2048f) / 2048f;

            result.Add(new DoviTrimSnapshot(
                2, referenceNits,
                DoviTrimUnit(l2.trim_slope),
                DoviTrimSigned(l2.trim_offset),
                DoviTrimUnit(l2.trim_power),
                DoviTrimSigned(l2.trim_chroma_weight),
                DoviTrimSigned(l2.trim_saturation_gain),
                msFactor));
        }

        return [.. result];
    }

    DoviTrimSnapshot FLDoviSelectTrim(ReadOnlySpan<DoviTrimSnapshot> candidates)
    {
        float targetNits = hdrData.TargetPeakNits;
        if (!float.IsFinite(targetNits) || targetNits <= 0)
            return default;

        float targetPQ = NitsToPQ(targetNits);
        float lowPQ = -1f, highPQ = 2f;
        float exactDistance = float.MaxValue;
        DoviTrimSnapshot low = default, high = default, exact = default;

        foreach (var trim in candidates)
        {
            float pq = NitsToPQ(trim.TargetNits);
            bool takeLow = pq <= targetPQ && pq > lowPQ;
            bool takeHigh = pq >= targetPQ && pq < highPQ;
            float distance = MathF.Abs(pq - targetPQ);
            bool takeExact = distance <= 0.5f / 4095f && distance < exactDistance;
            if (takeLow)   { lowPQ = pq; low = trim; }
            if (takeHigh)  { highPQ = pq; high = trim; }
            if (takeExact) { exactDistance = distance; exact = trim; }
        }

        if (exact.Level != 0)
            return hdrData.NativeOutput != 0 ? exact : exact with { MsWeight = 0f };
        if (low.Level == 0 && high.Level == 0)
            return default;

        DoviTrimSnapshot selected;
        if (low.Level == 0)
            selected = high;
        else if (high.Level == 0)
            selected = low;
        else if (highPQ - lowPQ <= 1f / 4095f)
            selected = low;
        else
        {
            float t = Math.Clamp((targetPQ - lowPQ) / (highPQ - lowPQ), 0f, 1f);
            selected = DoviInterpolateTrim(low, high, targetNits, t);
        }
        return hdrData.NativeOutput != 0 ? selected : selected with { MsWeight = 0f };
    }

    static DoviTargetGamut FLDoviPrimaries(AVColorPrimariesDesc primaries)
    {
        if (!DoviTryFloat(primaries.wp.x, out float wx) ||
            !DoviTryFloat(primaries.wp.y, out float wy) ||
            MathF.Abs(wx - 0.3127f) > 0.005f || MathF.Abs(wy - 0.329f) > 0.005f ||
            !DoviTryFloat(primaries.prim.r.x, out float rx) ||
            !DoviTryFloat(primaries.prim.r.y, out float ry) ||
            !DoviTryFloat(primaries.prim.g.x, out float gx) ||
            !DoviTryFloat(primaries.prim.g.y, out float gy) ||
            !DoviTryFloat(primaries.prim.b.x, out float bx) ||
            !DoviTryFloat(primaries.prim.b.y, out float by))
            return DoviTargetGamut.Unknown;

        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.005f;
        if (Near(rx, 0.708f) && Near(ry, 0.292f) && Near(gx, 0.170f) &&
            Near(gy, 0.797f) && Near(bx, 0.131f) && Near(by, 0.046f))
            return DoviTargetGamut.BT2020;
        if (Near(rx, 0.680f) && Near(ry, 0.320f) && Near(gx, 0.265f) &&
            Near(gy, 0.690f) && Near(bx, 0.150f) && Near(by, 0.060f))
            return DoviTargetGamut.P3;
        if (Near(rx, 0.640f) && Near(ry, 0.330f) && Near(gx, 0.300f) &&
            Near(gy, 0.600f) && Near(bx, 0.150f) && Near(by, 0.060f))
            return DoviTargetGamut.BT709;
        return DoviTargetGamut.Unknown;
    }

    DoviTargetGamut FLDoviSourceGamut(AVDOVIMetadata* dovi)
    {
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level == 9)
                return doviCachedSourceGamut = FLDoviPrimaries(dm->union0.l9.source_display_primaries);
        }
        return doviCachedSourceGamut;
    }

    static DoviL8Target? DoviKnownL8Target(byte id) => id switch
    {
         1 => new(id, 100, 0, DoviTargetGamut.BT709, false, 0),
        16 or 18 or 21 => new(id, 48, 0, DoviTargetGamut.P3, false, 0),
        24 => new(id, 300, 0, DoviTargetGamut.P3, true, 0),
        25 => new(id, 300, 0, DoviTargetGamut.BT2020, true, 0),
        27 => new(id, 600, 0, DoviTargetGamut.P3, true, 0),
        28 => new(id, 600, 0, DoviTargetGamut.BT2020, true, 0),
        37 => new(id, 2000, 0, DoviTargetGamut.P3, true, 0),
        38 => new(id, 2000, 0, DoviTargetGamut.BT2020, true, 0),
        42 => new(id, 108, 0, DoviTargetGamut.P3, true, 0),
        48 => new(id, 1000, 0, DoviTargetGamut.P3, true, 0),
        49 => new(id, 1000, 0, DoviTargetGamut.BT2020, true, 0),
        _ => null
    };

    void FLDoviUpdateL10(AVDOVIMetadata* dovi)
    {
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level != 10)
                continue;

            var l10 = dm->union0.l10;
            if (l10.target_max_pq is 0 or > 4095 ||
                l10.target_min_pq > l10.target_max_pq)
                continue;

            byte id = l10.target_display_index;
            var known = DoviKnownL8Target(id);
            var gamut = FLDoviPrimaries(l10.target_display_primaries);
            doviCachedL10[id] = new(
                id, PQToNitsDovi(l10.target_max_pq), PQToNitsDovi(l10.target_min_pq),
                gamut != DoviTargetGamut.Unknown ? gamut : known?.Gamut ?? DoviTargetGamut.Unknown,
                known?.IsPQ ?? true, l10.target_primary_index);
        }
    }

    DoviL8Target? DoviResolveL8Target(byte id)
        => doviCachedL10.TryGetValue(id, out var cached) ? cached : DoviKnownL8Target(id);

    DoviL8Snapshot[] FLDoviReadL8(AVDOVIMetadata* dovi)
    {
        List<DoviL8Snapshot> result = [];
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi + dovi->ext_block_offset +
                dovi->ext_block_size * (nuint)i);
            if (dm->level != 8)
                continue;

            var l8 = dm->union0.l8;
            if (l8.trim_slope > 4095 || l8.trim_offset > 4095 || l8.trim_power > 4095 ||
                l8.trim_chroma_weight > 4095 || l8.trim_saturation_gain > 4095 ||
                l8.ms_weight > 4095)
                continue;
            if (DoviResolveL8Target(l8.target_display_index) is not { } target ||
                target.PeakNits <= 0)
                continue;

            DoviL8Vector saturation = new(
                l8.saturation_vector_field[0], l8.saturation_vector_field[1],
                l8.saturation_vector_field[2], l8.saturation_vector_field[3],
                l8.saturation_vector_field[4], l8.saturation_vector_field[5]);
            DoviL8Vector hue = new(
                l8.hue_vector_field[0], l8.hue_vector_field[1],
                l8.hue_vector_field[2], l8.hue_vector_field[3],
                l8.hue_vector_field[4], l8.hue_vector_field[5]);

            // FFmpeg drops L8 block lengths; later fields imply earlier ones exist.
            bool hasHue  = DoviL8VectorHasValues(hue);
            bool hasSat  = hasHue || DoviL8VectorHasValues(saturation);
            bool hasClip = hasSat || l8.clip_trim != 0;
            bool hasMid  = hasClip || l8.target_mid_contrast != 0;

            result.Add(new DoviL8Snapshot(
                8, target,
                DoviTrimUnit(l8.trim_slope), DoviTrimSigned(l8.trim_offset),
                DoviTrimUnit(l8.trim_power), DoviTrimSigned(l8.trim_chroma_weight),
                DoviTrimSigned(l8.trim_saturation_gain),
                (ushort)l8.ms_weight, l8.target_mid_contrast, l8.clip_trim,
                saturation, hue, hasMid, hasClip, hasSat, hasHue));
        }
        return [.. result];
    }

    static bool DoviL8VectorHasValues(DoviL8Vector v)
        => (v.V0 | v.V1 | v.V2 | v.V3 | v.V4 | v.V5) != 0;

    DoviL8Snapshot FLDoviSelectL8(ReadOnlySpan<DoviL8Snapshot> candidates)
    {
        float peak = hdrData.TargetPeakNits;
        if (!float.IsFinite(peak) || peak <= 0)
            return default;

        bool native = hdrData.NativeOutput != 0;
        float targetPQ = NitsToPQ(peak);
        float blackPQ = NitsToPQ(MathF.Max(hdrData.TargetMinNits, 0));
        float bestScore = float.MaxValue;
        DoviL8Snapshot selected = default;

        foreach (var l8 in candidates)
        {
            var target = l8.Target;
            if (target.IsPQ != native || (native
                ? target.Gamut is not (DoviTargetGamut.P3 or DoviTargetGamut.BT2020)
                : target.Gamut != DoviTargetGamut.BT709))
                continue;

            float score = MathF.Abs(targetPQ - NitsToPQ(target.PeakNits));
            if (target.MinNits > 0)
                score += 0.05f * MathF.Abs(blackPQ - NitsToPQ(target.MinNits));
            if (score < bestScore)
            {
                bestScore = score;
                selected = l8;
            }
        }
        return selected;
    }

    static void DoviWriteTrim(ref DoviBufferType buffer, DoviTrimSnapshot trim)
    {
        DoviSet(ref buffer, doviTrimInfo,
            new(trim.Level, trim.TargetNits, 0, 0));
        DoviSet(ref buffer, doviTrimPrimary,
            new(trim.Slope, trim.Offset, trim.Power, trim.ChromaWeight));
        DoviSet(ref buffer, doviTrimSecondary,
            new(trim.SaturationGain, trim.MsWeight, 0, 0));
    }

    static float DoviL8OptionalTrim(ushort code, bool present) => present ? DoviTrimSigned(code) : 0f;
    static float DoviL8VectorTrim(byte code, bool present) => present ? (code - 128f) / 128f : 0f;

    static void DoviWriteL8(ref DoviBufferType buffer, DoviL8Snapshot l8)
    {
        DoviSet(ref buffer, doviL8Info,
            new(l8.Level, l8.Target.PeakNits,
                DoviL8OptionalTrim(l8.MidContrast, l8.HasMidContrast),
                DoviL8OptionalTrim(l8.ClipTrim, l8.HasClipTrim)));
        DoviSet(ref buffer, doviL8Primary,
            new(l8.Slope, l8.Offset, l8.Power, l8.ChromaWeight));
        DoviSet(ref buffer, doviL8Secondary,
            new(l8.SaturationGain, l8.Level == 8 ? (l8.MsWeight - 2048f) / 2048f : 0f, 0, 0));
        DoviSet(ref buffer, doviL8Saturation0,
            new(DoviL8VectorTrim(l8.Saturation.V0, l8.HasSaturation), DoviL8VectorTrim(l8.Saturation.V1, l8.HasSaturation),
                DoviL8VectorTrim(l8.Saturation.V2, l8.HasSaturation), DoviL8VectorTrim(l8.Saturation.V3, l8.HasSaturation)));
        DoviSet(ref buffer, doviL8Saturation1Hue0,
            new(DoviL8VectorTrim(l8.Saturation.V4, l8.HasSaturation), DoviL8VectorTrim(l8.Saturation.V5, l8.HasSaturation),
                DoviL8VectorTrim(l8.Hue.V0, l8.HasHue), DoviL8VectorTrim(l8.Hue.V1, l8.HasHue)));
        DoviSet(ref buffer, doviL8Hue1,
            new(DoviL8VectorTrim(l8.Hue.V2, l8.HasHue), DoviL8VectorTrim(l8.Hue.V3, l8.HasHue),
                DoviL8VectorTrim(l8.Hue.V4, l8.HasHue), DoviL8VectorTrim(l8.Hue.V5, l8.HasHue)));
    }

    void FLDoviApply(VideoFrame frame, bool felReady)
    {
        var data = frame.Dovi;
        FLDoviSetL5(data?.L5Bounds ?? default);

        if (data != null)
        {
            if (hdrData.NativeOutput == 0)
                FLGamutUpdateTarget(data.IsCM4 == true ? data.SourceGamut : DoviTargetGamut.Unknown);

            // Queued frames retain metadata; display-dependent trims are selected here.
            var trim = data.IsCM4 == false ? FLDoviSelectTrim(data.L2Targets) : default;
            var l8 = data.IsCM4 == true ? FLDoviSelectL8(data.L8Targets) : default;
            Vector4 activeFEL = felReady ? felInfo : default;
            if (!ReferenceEquals(data, doviAppliedData) || doviAppliedFELInfo != activeFEL ||
                doviAppliedTrim != trim || doviAppliedL8 != l8)
            {
                DoviBufferType buffer = data.Buffer;
                if (data.L5Bounds != default)
                    DoviSet(ref buffer, doviL5Bounds, data.L5Bounds);
                if (data.Trim != trim)
                    DoviWriteTrim(ref buffer, trim);
                if (data.L8 != l8)
                    DoviWriteL8(ref buffer, l8);
                if (felReady)
                    DoviSet(ref buffer, doviFelInfo, activeFEL);

                context.UpdateSubresource(buffer, doviBuffer);
                doviAppliedData    = data;
                doviAppliedTrim    = trim;
                doviAppliedL8      = l8;
                doviAppliedFELInfo = activeFEL;
                doviAppliedEmpty   = false;
            }

            var l1 = frame.DoviL1Nits.GetValueOrDefault();
            bool detect = DoviUseHDRDetect || data.IsCM4 == null || !frame.DoviL1Nits.HasValue;
            if (detect != doviAppliedHDRDetect)
            {
                doviAppliedHDRDetect = detect;
                if (detect)
                    FLHDRDetectReset();
                else
                    doviL1Nits = null;
            }

            if (detect)
            {
                doviL1Nits = null;
                FLHDRDetect();
            }
            else if (doviL1Nits != l1 ||
                     doviL1TargetMin != hdrData.TargetMinNits ||
                     doviL1TargetPeak != hdrData.TargetPeakNits)
            {
                doviL1Nits = l1;
                doviL1TargetMin = hdrData.TargetMinNits;
                doviL1TargetPeak = hdrData.TargetPeakNits;

                hdrData.SourceMinNits   = l1.Min;
                hdrData.SourcePeakNits  = l1.Max;
                float splinePeakNits    = MathF.Max(l1.Max, hdrData.TargetPeakNits);

                hdrData.Spline = GetSplineParams(
                    sourceMinNits:  l1.Min,
                    sourceAvgNits:  l1.Avg,
                    sourcePeakNits: splinePeakNits,
                    targetMinNits:  hdrData.TargetMinNits,
                    targetPeakNits: hdrData.TargetPeakNits);

                context.UpdateSubresource(hdrData, hdrBuffer);
            }

            return;
        }

        if (doviAppliedEmpty)
            return;

        DoviBufferType empty= default;
        context.UpdateSubresource(empty, doviBuffer);
        doviAppliedData     = null;
        doviAppliedTrim     = default;
        doviAppliedL8       = default;
        doviAppliedFELInfo  = default;
        doviAppliedEmpty    = true;
        doviL1Nits          = null;
        doviAppliedHDRDetect = false;
    }

    static bool DoviTryFloat(AVRational value, out float result)
    {
        if (value.Den == 0)
        {
            result = 0;
            return false;
        }

        result = (float)value.Num / value.Den;
        return float.IsFinite(result);
    }

    static bool DoviFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    static void DoviSet(ref DoviBufferType buffer, int index, Vector4 value)
    {
        fixed (float* p = buffer.Data)
            ((Vector4*)p)[index] = value;
    }

    bool DoviSourceEquals(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping,
                          AVDOVIColorMetadata* color, bool hasFEL)
    {
        if (!doviSourceCached ||
            header->bl_bit_depth    != doviCachedBlBitDepth     ||
            header->coef_log2_denom != doviCachedCoefLog2Denom  ||
            hasFEL                  != doviCachedFEL            ||
            (hasFEL && header->el_bit_depth != doviCachedElBitDepth))
            return false;

        if (hasFEL &&
            (!DoviNLQEquals(mapping->nlq[0], doviCachedNLQ0) ||
             !DoviNLQEquals(mapping->nlq[1], doviCachedNLQ1) ||
             !DoviNLQEquals(mapping->nlq[2], doviCachedNLQ2)))
            return false;

        ref var curve0 = ref mapping->curves[0];
        ref var curve1 = ref mapping->curves[1];
        ref var curve2 = ref mapping->curves[2];

        if (!DoviCurveEquals(ref curve0, ref doviCachedCurve0) ||
            !DoviCurveEquals(ref curve1, ref doviCachedCurve1) ||
            !DoviCurveEquals(ref curve2, ref doviCachedCurve2))
            return false;

        for (int i = 0; i < 9; i++)
            if (!DoviRationalEquals(color->ycc_to_rgb_matrix[i], doviCachedColor.ycc_to_rgb_matrix[i]) ||
                !DoviRationalEquals(color->rgb_to_lms_matrix[i], doviCachedColor.rgb_to_lms_matrix[i]))
                return false;

        for (int i = 0; i < 3; i++)
            if (!DoviRationalEquals(color->ycc_to_rgb_offset[i], doviCachedColor.ycc_to_rgb_offset[i]))
                return false;

        return true;
    }

    void DoviCacheSource(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping,
                         AVDOVIColorMetadata* color, bool hasFEL)
    {
        doviCachedBlBitDepth     = header->bl_bit_depth;
        doviCachedCoefLog2Denom  = header->coef_log2_denom;
        doviCachedCurve0         = mapping->curves[0];
        doviCachedCurve1         = mapping->curves[1];
        doviCachedCurve2         = mapping->curves[2];
        doviCachedColor          = *color;
        doviCachedFEL            = hasFEL;
        doviCachedElBitDepth     = header->el_bit_depth;
        if (doviCachedFEL)
        {
            doviCachedNLQ0       = mapping->nlq[0];
            doviCachedNLQ1       = mapping->nlq[1];
            doviCachedNLQ2       = mapping->nlq[2];
        }
        doviSourceCached         = true;
    }

    static bool DoviNLQEquals(AVDOVINLQParams a, AVDOVINLQParams b)
        => a.nlq_offset == b.nlq_offset &&
           a.vdr_in_max == b.vdr_in_max &&
           a.linear_deadzone_slope == b.linear_deadzone_slope &&
           a.linear_deadzone_threshold == b.linear_deadzone_threshold;

    static bool DoviCurveEquals(ref AVDOVIReshapingCurve a, ref AVDOVIReshapingCurve b)
    {
        var ba = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref a, 1));
        var bb = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b, 1));
        return ba.SequenceEqual(bb);
    }

    static bool DoviRationalEquals(AVRational a, AVRational b)
        => a.Num == b.Num && a.Den == b.Den;

    static void DoviMul3x3(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        for (int r = 0; r < 3; r++)
        {
            int ri = r * 3;
            for (int c = 0; c < 3; c++)
            {
                dst[ri + c] =
                    a[ri]     * b[c] +
                    a[ri + 1] * b[3 + c] +
                    a[ri + 2] * b[6 + c];
            }
        }
    }

    static void DoviMulRow3x3(ReadOnlySpan<float> row, ReadOnlySpan<float> matrix, Span<float> dst)
    {
        dst[0] = row[0] * matrix[0] + row[1] * matrix[3] + row[2] * matrix[6];
        dst[1] = row[0] * matrix[1] + row[1] * matrix[4] + row[2] * matrix[7];
        dst[2] = row[0] * matrix[2] + row[1] * matrix[5] + row[2] * matrix[8];
    }

    string FLDoviDump(AVFrame* frame)
    {
        if (frame == null)
            return null;

        var side = av_frame_side_data_get(frame->side_data, frame->nb_side_data, AVFrameSideDataType.DOVIMetadata);

        if (side == null || side->data == null)
            return null;

        var dovi    = (AVDOVIMetadata*)side->data;
        var header  = (AVDOVIRpuDataHeader*)((byte*)dovi + dovi->header_offset);
        var mapping = (AVDOVIDataMapping*)  ((byte*)dovi + dovi->mapping_offset);
        var color   = (AVDOVIColorMetadata*)((byte*)dovi + dovi->color_offset);

        static double DoviDumpRational(AVRational value) => value.Den == 0 ? double.NaN : (double)value.Num / value.Den;

        StringBuilder sb = new();
        
        sb.AppendLine("");
        sb.AppendLine("============================================================");
        sb.AppendLine("DOLBY VISION METADATA");
        sb.AppendLine("============================================================");

        sb.AppendLine("");
        sb.AppendLine("[AVDOVIMetadata]");
        sb.AppendLine($"  header_offset  = {dovi->header_offset}");
        sb.AppendLine($"  mapping_offset = {dovi->mapping_offset}");
        sb.AppendLine($"  color_offset   = {dovi->color_offset}");
        sb.AppendLine($"  ext_mapping_idc_0_4              = {header->ext_mapping_idc_0_4}");
        sb.AppendLine($"  ext_mapping_idc_5_7              = {header->ext_mapping_idc_5_7}");

        sb.AppendLine("");
        sb.AppendLine("[MAPPING HEADER]");
        sb.AppendLine($"  vdr_rpu_id                       = {mapping->vdr_rpu_id}");
        sb.AppendLine($"  mapping_color_space              = {mapping->mapping_color_space}");
        sb.AppendLine($"  mapping_chroma_format_idc        = {mapping->mapping_chroma_format_idc}");
        sb.AppendLine($"  nlq_method_idc                   = {mapping->nlq_method_idc}");
        sb.AppendLine($"  num_x_partitions                 = {mapping->num_x_partitions}");
        sb.AppendLine($"  num_y_partitions                 = {mapping->num_y_partitions}");

        sb.AppendLine("");
        sb.AppendLine("[RPU HEADER]");

        sb.AppendLine($"  rpu_type                        = {header->rpu_type}");
        sb.AppendLine($"  rpu_format                      = {header->rpu_format}");
        sb.AppendLine($"  vdr_rpu_profile                 = {header->vdr_rpu_profile}");
        sb.AppendLine($"  vdr_rpu_level                   = {header->vdr_rpu_level}");

        sb.AppendLine($"  chroma_resampling_explicit_filter_flag = {header->chroma_resampling_explicit_filter_flag}");

        sb.AppendLine($"  coef_data_type                  = {header->coef_data_type}");
        sb.AppendLine($"  coef_log2_denom                 = {header->coef_log2_denom}");

        sb.AppendLine($"  vdr_rpu_normalized_idc          = {header->vdr_rpu_normalized_idc}");
        sb.AppendLine($"  bl_video_full_range_flag        = {header->bl_video_full_range_flag}");

        sb.AppendLine($"  bl_bit_depth                    = {header->bl_bit_depth}");
        sb.AppendLine($"  el_bit_depth                    = {header->el_bit_depth}");
        sb.AppendLine($"  vdr_bit_depth                   = {header->vdr_bit_depth}");

        sb.AppendLine($"  spatial_resampling_filter_flag  = {header->spatial_resampling_filter_flag}");
        sb.AppendLine($"  el_spatial_resampling_filter_flag = {header->el_spatial_resampling_filter_flag}");

        sb.AppendLine($"  disable_residual_flag           = {header->disable_residual_flag}");

        sb.AppendLine("");
        sb.AppendLine("[COLOR METADATA]");

        sb.AppendLine("  ycc_to_rgb_matrix:");
        for (int r = 0; r < 3; r++)
        {
            sb.Append("    ");

            for (int c = 0; c < 3; c++)
            {
                AVRational v = color->ycc_to_rgb_matrix[r * 3 + c];

                sb.Append(
                    $"{v.Num}/{v.Den} ({DoviDumpRational(v):F10}) ");
            }

            sb.AppendLine("");
        }

        sb.AppendLine("  ycc_to_rgb_offset:");
        for (int i = 0; i < 3; i++)
        {
            AVRational v = color->ycc_to_rgb_offset[i];

            sb.AppendLine(
                $"    [{i}] = {v.Num}/{v.Den} ({DoviDumpRational(v):F10})");
        }

        sb.AppendLine("  rgb_to_lms_matrix:");
        for (int r = 0; r < 3; r++)
        {
            sb.Append("    ");

            for (int c = 0; c < 3; c++)
            {
                AVRational v = color->rgb_to_lms_matrix[r * 3 + c];

                sb.Append(
                    $"{v.Num}/{v.Den} ({DoviDumpRational(v):F10}) ");
            }

            sb.AppendLine("");
        }

        sb.AppendLine("");
        sb.AppendLine("[COLOR SIGNAL METADATA]");
        sb.AppendLine($"  dm_metadata_id          = {color->dm_metadata_id}");
        sb.AppendLine($"  scene_refresh_flag      = {color->scene_refresh_flag}");
        sb.AppendLine($"  signal_eotf             = {color->signal_eotf}");
        sb.AppendLine($"  signal_eotf_param0      = {color->signal_eotf_param0}");
        sb.AppendLine($"  signal_eotf_param1      = {color->signal_eotf_param1}");
        sb.AppendLine($"  signal_eotf_param2      = {color->signal_eotf_param2}");
        sb.AppendLine($"  signal_bit_depth        = {color->signal_bit_depth}");
        sb.AppendLine($"  signal_color_space      = {color->signal_color_space}");
        sb.AppendLine($"  signal_chroma_format    = {color->signal_chroma_format}");
        sb.AppendLine($"  signal_full_range_flag  = {color->signal_full_range_flag}");
        sb.AppendLine($"  source_min_pq           = {color->source_min_pq}");
        sb.AppendLine($"  source_max_pq           = {color->source_max_pq}");
        sb.AppendLine($"  source_diagonal         = {color->source_diagonal}");
        sb.AppendLine("");
        sb.AppendLine("[RESHAPING MAPPING]");

        for (int component = 0; component < 3; component++)
        {
            ref var curve = ref mapping->curves[component];

            sb.AppendLine("");
            sb.AppendLine($"  COMPONENT {component}");
            sb.AppendLine($"    num_pivots = {curve.num_pivots}");

            sb.Append("    pivots = ");

            for (int i = 0; i < curve.num_pivots; i++)
            {
                if (i != 0)
                    sb.Append(", ");

                sb.Append(curve.pivots[i]);
            }

            sb.AppendLine("");

            int pieces = curve.num_pivots - 1;

            for (int p = 0; p < pieces; p++)
            {
                sb.AppendLine("");
                sb.AppendLine($"    PIECE {p}");
                sb.AppendLine($"      mapping_idc = {curve.mapping_idc[p]}");

                switch (curve.mapping_idc[p])
                {
                    case AVDOVIMappingMethod.Polynomial:
                    {
                        int order = curve.poly_order[p];

                        sb.AppendLine($"      poly_order = {order}");

                        for (int i = 0; i <= order; i++)
                        {
                            long raw = curve.poly_coef[p][i];

                            double scaled =
                                raw * Math.ScaleB(1.0, -header->coef_log2_denom);

                            sb.AppendLine(
                                $"      poly_coef[{i}] = {raw} -> {scaled:F12}");
                        }

                        break;
                    }

                    case AVDOVIMappingMethod.Mmr:
                    {
                        int order = curve.mmr_order[p];

                        sb.AppendLine($"      mmr_order    = {order}");

                        {
                            long raw = curve.mmr_constant[p];

                            double scaled =
                                raw * Math.ScaleB(1.0, -header->coef_log2_denom);

                            sb.AppendLine(
                                $"      mmr_constant = {raw} -> {scaled:F12}");
                        }

                        for (int o = 0; o < order; o++)
                        {
                            sb.AppendLine($"      MMR ORDER {o + 1}:");

                            for (int i = 0; i < 7; i++)
                            {
                                long raw = curve.mmr_coef[p][o][i];

                                double scaled =
                                    raw * Math.ScaleB(1.0, -header->coef_log2_denom);

                                sb.AppendLine(
                                    $"        coef[{i}] = {raw} -> {scaled:F12}");
                            }
                        }

                        break;
                    }

                    default:
                        sb.AppendLine("      *** UNKNOWN MAPPING TYPE ***");
                        break;
                }
            }
        }

        sb.AppendLine("");
        sb.AppendLine("[DM EXTENSION BLOCKS]");
        sb.AppendLine($"  ext_block_offset = {dovi->ext_block_offset}");
        sb.AppendLine($"  ext_block_size   = {dovi->ext_block_size}");
        sb.AppendLine($"  num_ext_blocks   = {dovi->num_ext_blocks}");
        for (int i = 0; i < dovi->num_ext_blocks; i++)
        {
            var dm = (AVDOVIDmData*)((byte*)dovi +dovi->ext_block_offset +dovi->ext_block_size * (nuint)i);

            sb.AppendLine("");
            sb.AppendLine($"  [{i}] LEVEL {dm->level}");

            switch (dm->level)
            {
                case 1:
                {   // Min / Mid / Max brightness
                    var l = dm->union0.l1;

                    sb.AppendLine($"    min_pq  = {l.min_pq}");
                    sb.AppendLine($"    max_pq  = {l.max_pq}");
                    sb.AppendLine($"    avg_pq  = {l.avg_pq}");
                    break;
                }

                case 2:
                {   // CM 2.9 creative trims per target display
                    var l = dm->union0.l2;

                    sb.AppendLine($"    target_max_pq        = {l.target_max_pq}");
                    sb.AppendLine($"    trim_slope           = {l.trim_slope}");
                    sb.AppendLine($"    trim_offset          = {l.trim_offset}");
                    sb.AppendLine($"    trim_power           = {l.trim_power}");
                    sb.AppendLine($"    trim_chroma_weight   = {l.trim_chroma_weight}");
                    sb.AppendLine($"    trim_saturation_gain = {l.trim_saturation_gain}");
                    sb.AppendLine($"    ms_weight            = {l.ms_weight}");
                    break;
                }

                case 3:
                {   // Corrections to L1 statistics
                    var l = dm->union0.l3;

                    sb.AppendLine($"    min_pq_offset = {l.min_pq_offset}");
                    sb.AppendLine($"    max_pq_offset = {l.max_pq_offset}");
                    sb.AppendLine($"    avg_pq_offset = {l.avg_pq_offset}");
                    break;
                }

                case 4:
                {   // Anchor PQ / power metadata
                    var l = dm->union0.l4;

                    sb.AppendLine($"    anchor_pq    = {l.anchor_pq}");
                    sb.AppendLine($"    anchor_power = {l.anchor_power}");
                    break;
                }

                case 5:
                {   // Active picture area / black-bar boundaries
                    var l = dm->union0.l5;

                    sb.AppendLine($"    left_offset   = {l.left_offset}");
                    sb.AppendLine($"    right_offset  = {l.right_offset}");
                    sb.AppendLine($"    top_offset    = {l.top_offset}");
                    sb.AppendLine($"    bottom_offset = {l.bottom_offset}");
                    break;
                }

                case 6:
                {   // Static HDR10 mastering and content metadata
                    var l = dm->union0.l6;

                    sb.AppendLine($"    max_luminance = {l.max_luminance}");
                    sb.AppendLine($"    min_luminance = {l.min_luminance}");
                    sb.AppendLine($"    max_cll       = {l.max_cll}");
                    sb.AppendLine($"    max_fall      = {l.max_fall}");
                    break;
                }

                case 8:
                {   // CM 4.0 extended creative trims
                    var l = dm->union0.l8;

                    sb.AppendLine($"    target_display_index = {l.target_display_index}");
                    sb.AppendLine($"    trim_slope           = {l.trim_slope}");
                    sb.AppendLine($"    trim_offset          = {l.trim_offset}");
                    sb.AppendLine($"    trim_power           = {l.trim_power}");
                    sb.AppendLine($"    trim_chroma_weight   = {l.trim_chroma_weight}");
                    sb.AppendLine($"    trim_saturation_gain = {l.trim_saturation_gain}");
                    sb.AppendLine($"    ms_weight            = {l.ms_weight}");
                    sb.AppendLine($"    target_mid_contrast  = {l.target_mid_contrast}");
                    sb.AppendLine($"    clip_trim            = {l.clip_trim}");

                    sb.Append("    saturation_vector    = ");
                    for (int j = 0; j < 6; j++)
                    {
                        if (j != 0)
                            sb.Append(", ");

                        sb.Append(l.saturation_vector_field[j]);
                    }
                    sb.AppendLine();

                    sb.Append("    hue_vector           = ");
                    for (int j = 0; j < 6; j++)
                    {
                        if (j != 0)
                            sb.Append(", ");

                        sb.Append(l.hue_vector_field[j]);
                    }
                    sb.AppendLine();
                    break;
                }

                case 9:
                {   // Source mastering-display primaries
                    var l = dm->union0.l9;

                    sb.AppendLine($"    source_primary_index = {l.source_primary_index}");

                    // source_display_primaries is AVColorPrimariesDesc.
                    // Dump separately if useful.
                    break;
                }

                case 10:
                {   // Target-display characteristics
                    var l = dm->union0.l10;

                    sb.AppendLine($"    target_display_index = {l.target_display_index}");
                    sb.AppendLine($"    target_max_pq        = {l.target_max_pq}");
                    sb.AppendLine($"    target_min_pq        = {l.target_min_pq}");
                    sb.AppendLine($"    target_primary_index = {l.target_primary_index}");

                    // target_display_primaries is AVColorPrimariesDesc.
                    // Dump separately if useful.
                    break;
                }

                case 11:
                {   // Content type / white-point intent (APO)
                    var l = dm->union0.l11;

                    sb.AppendLine($"    content_type        = {l.content_type}");
                    sb.AppendLine($"    whitepoint          = {l.whitepoint}");
                    sb.AppendLine($"    reference_mode_flag = {l.reference_mode_flag}");
                    break;
                }

                case 254:
                {   // Content-mapping version information
                    var l = dm->union0.l254;

                    sb.AppendLine($"    dm_mode          = {l.dm_mode}");
                    sb.AppendLine($"    dm_version_index = {l.dm_version_index}");
                    break;
                }

                case 255:
                {   // Debug metadata
                    var l = dm->union0.l255;

                    sb.AppendLine($"    dm_run_mode    = {l.dm_run_mode}");
                    sb.AppendLine($"    dm_run_version = {l.dm_run_version}");

                    sb.Append("    dm_debug       = ");
                    for (int j = 0; j < 4; j++)
                    {
                        if (j != 0)
                            sb.Append(", ");

                        sb.Append(l.dm_debug[j]);
                    }
                    sb.AppendLine();
                    break;
                }

                default:
                    sb.AppendLine("    (unknown / unsupported level)");
                    break;
            }
		}
	
	    sb.AppendLine("");
	    sb.AppendLine("============================================================");

        return sb.ToString();
	}
}
