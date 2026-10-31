using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D11;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    const int doviVectors             = 187;
    const int doviComponentVectors    = 59;
    const int doviComponentBase       = 10;
    const int doviMmrVectors          = 48;
    const float doviPivotSentinel     = 1e9f;

    // Dovi[0]      : sampleScale, identityReshape, 0, 0
    // Dovi[1..3]   : affine YCC -> nonlinear RGB rows
    // Dovi[4..6]   : linear DOVI RGB -> BT.2020 rows, BT.2020 luma coefficients in .w
    // Dovi[7..9]   : linear DOVI RGB -> BT.709 rows
    // Dovi[10..]   : three reshape components, 59 float4s each
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

    internal sealed class DoviFrameData
    {
        public DoviBufferType Buffer;
    }

    // Prepare-side cache: consecutive frames commonly repeat the same RPU mapping.
    // Reuse the immutable DoviFrameData instead of allocating one object per frame.
    DoviFrameData doviPreparedData;

    // Render-side cache: if consecutive frames share the prepared state, don't upload b2 again.
    DoviFrameData doviAppliedData;
    bool doviAppliedEmpty;

    // Prepare-side source cache. FFmpeg attaches a fresh AVDOVIMetadata copy to
    // every frame, so pointer identity cannot be used. Keep only the metadata
    // that actually affects our shader and compare it before rebuilding the
    // 187-vector GPU buffer.
    bool                    doviSourceCached;
    byte                    doviCachedBlBitDepth;
    byte                    doviCachedCoefLog2Denom;
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
        if (scfg == null || !scfg.DoviRpuPresent || !scfg.DoviBlPresent)
            return false;

        return scfg.DoviProfile switch
        {
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

    void FLDoviReset()
    {
        doviPreparedData      = null;
        doviAppliedData       = null;
        doviAppliedEmpty      = false;
        doviSourceCached      = false;
        doviCachedColor       = default;
        doviCachedCurve0      = default;
        doviCachedCurve1      = default;
        doviCachedCurve2      = default;
    }

    DoviFrameData FLDoviPrepare(AVFrame* frame)
    {
        var side = av_frame_side_data_get(frame->side_data, frame->nb_side_data, AVFrameSideDataType.DoviMetadata);

        if (side == null || side->data == null)
            return null;

        var dovi    = (AVDOVIMetadata*)side->data;
        var header  = (AVDOVIRpuDataHeader*)((byte*)dovi + dovi->header_offset);
        var mapping = (AVDOVIDataMapping*)  ((byte*)dovi + dovi->mapping_offset);
        var color   = (AVDOVIColorMetadata*)((byte*)dovi + dovi->color_offset);

        // FFmpeg's valid coefficient denominator range is [13, 32]. Validate it
        // before ScaleB/bit shifts below so malformed side data cannot produce
        // invalid math or an oversized shift.
        if (header->coef_log2_denom < 13 || header->coef_log2_denom > 32)
            return null;

        // Fast path for the overwhelmingly common case where consecutive frames
        // reuse the same reshape/matrix state. The side-data allocation itself is
        // new every frame, so compare the source metadata rather than pointers.
        if (doviPreparedData != null && DoviSourceEquals(header, mapping, color))
            return doviPreparedData;

        // FFmpeg defines BL depth as [8, 16]. Keep malformed/unsupported metadata
        // out of the shader rather than coercing it into a seemingly valid mapping.
        if (header->bl_bit_depth < 8 || header->bl_bit_depth > 16)
            return null;

        int sampleBits = scfg.PixelComp0Depth > 8 ? 16 : 8;
        if (header->bl_bit_depth > sampleBits)
            return null;

        uint sampleMax = (1u << sampleBits) - 1u;
        uint blMax     = (1u << header->bl_bit_depth) - 1u;
        int shift      = sampleBits - header->bl_bit_depth;

        float sampleScale = sampleMax / (blMax * (float)(1u << shift));
        float coefScale   = MathF.ScaleB(1.0f, -header->coef_log2_denom);
        float pivotScale  = 1.0f / blMax;

        if (!float.IsFinite(sampleScale) || !float.IsFinite(coefScale))
            return null;

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
                return null;

            for (int c = 0; c < 3; c++)
            {
                if (!DoviTryFloat(color->ycc_to_rgb_matrix[r * 3 + c], out ycc[r * 3 + c]) ||
                    !DoviTryFloat(color->rgb_to_lms_matrix[r * 3 + c], out rgbToLms[r * 3 + c]))
                    return null;
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
                return null;

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
                return null;

            ushort previous = curve.pivots[0];
            if (previous > blMax)
                return null;

            for (int i = 1; i < numPivots; i++)
            {
                ushort current = curve.pivots[i];
                if (current <= previous || current > blMax)
                    return null;
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
                            return null;

                        float c0 = curve.poly_coef[p][0] * coefScale;
                        float c1 = curve.poly_coef[p][1] * coefScale;
                        float c2 = order == 2 ? curve.poly_coef[p][2] * coefScale : 0;

                        if (!float.IsFinite(c0) || !float.IsFinite(c1) || !float.IsFinite(c2))
                            return null;

                        DoviSet(ref buffer, componentBase + 3 + p, new(c0, c1, c2, 0));
                        break;
                    }

                    case AVDOVIMappingMethod.Mmr:
                    {
                        int order = curve.mmr_order[p];
                        if (order < 1 || order > 3)
                            return null;

                        int vectors = order * 2;
                        if (mmrIndex + vectors > doviMmrVectors)
                            return null;

                        float constant = curve.mmr_constant[p] * coefScale;
                        if (!float.IsFinite(constant))
                            return null;

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
                                return null;

                            DoviSet(ref buffer, mmrBase,     a);
                            DoviSet(ref buffer, mmrBase + 1, b);

                            mmrIndex += 2;
                        }
                        break;
                    }

                    default:
                        return null;
                }
            }
        }

        DoviSet(ref buffer, 0, new(sampleScale, identityReshape ? 1.0f : 0.0f, 0, 0));

        DoviCacheSource(header, mapping, color);
        doviPreparedData = new() { Buffer = buffer };
        return doviPreparedData;
    }

    void FLDoviApply(VideoFrame frame)
    {
        var data = frame.Dovi;

        if (data != null)
        {
            if (ReferenceEquals(data, doviAppliedData))
                return;

            context.UpdateSubresource(data.Buffer, doviBuffer);
            doviAppliedData  = data;
            doviAppliedEmpty = false;
            return;
        }

        // A DOVI shader should normally always have RPU metadata. Clear once when
        // metadata is missing/invalid so an older frame's RPU is never reused.
        if (doviAppliedEmpty)
            return;

        DoviBufferType empty = default;
        context.UpdateSubresource(empty, doviBuffer);
        doviAppliedData  = null;
        doviAppliedEmpty = true;
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

    bool DoviSourceEquals(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping, AVDOVIColorMetadata* color)
    {
        if (!doviSourceCached ||
            header->bl_bit_depth    != doviCachedBlBitDepth ||
            header->coef_log2_denom != doviCachedCoefLog2Denom)
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

    void DoviCacheSource(AVDOVIRpuDataHeader* header, AVDOVIDataMapping* mapping, AVDOVIColorMetadata* color)
    {
        doviCachedBlBitDepth     = header->bl_bit_depth;
        doviCachedCoefLog2Denom  = header->coef_log2_denom;
        doviCachedCurve0         = mapping->curves[0];
        doviCachedCurve1         = mapping->curves[1];
        doviCachedCurve2         = mapping->curves[2];
        doviCachedColor          = *color;
        doviSourceCached         = true;
    }

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

        var side = av_frame_side_data_get(frame->side_data, frame->nb_side_data, AVFrameSideDataType.DoviMetadata);

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
                {
                    var l = dm->union0.l1;

                    sb.AppendLine($"    min_pq  = {l.min_pq}");
                    sb.AppendLine($"    max_pq  = {l.max_pq}");
                    sb.AppendLine($"    avg_pq  = {l.avg_pq}");
                    break;
                }

                case 2:
                {
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
                {
                    var l = dm->union0.l3;

                    sb.AppendLine($"    min_pq_offset = {l.min_pq_offset}");
                    sb.AppendLine($"    max_pq_offset = {l.max_pq_offset}");
                    sb.AppendLine($"    avg_pq_offset = {l.avg_pq_offset}");
                    break;
                }

                case 4:
                {
                    var l = dm->union0.l4;

                    sb.AppendLine($"    anchor_pq    = {l.anchor_pq}");
                    sb.AppendLine($"    anchor_power = {l.anchor_power}");
                    break;
                }

                case 5:
                {
                    var l = dm->union0.l5;

                    sb.AppendLine($"    left_offset   = {l.left_offset}");
                    sb.AppendLine($"    right_offset  = {l.right_offset}");
                    sb.AppendLine($"    top_offset    = {l.top_offset}");
                    sb.AppendLine($"    bottom_offset = {l.bottom_offset}");
                    break;
                }

                case 6:
                {
                    var l = dm->union0.l6;

                    sb.AppendLine($"    max_luminance = {l.max_luminance}");
                    sb.AppendLine($"    min_luminance = {l.min_luminance}");
                    sb.AppendLine($"    max_cll       = {l.max_cll}");
                    sb.AppendLine($"    max_fall      = {l.max_fall}");
                    break;
                }

                case 8:
                {
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
                {
                    var l = dm->union0.l9;

                    sb.AppendLine($"    source_primary_index = {l.source_primary_index}");

                    // source_display_primaries is AVColorPrimariesDesc.
                    // Dump separately if useful.
                    break;
                }

                case 10:
                {
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
                {
                    var l = dm->union0.l11;

                    sb.AppendLine($"    content_type        = {l.content_type}");
                    sb.AppendLine($"    whitepoint          = {l.whitepoint}");
                    sb.AppendLine($"    reference_mode_flag = {l.reference_mode_flag}");
                    break;
                }

                case 254:
                {
                    var l = dm->union0.l254;

                    sb.AppendLine($"    dm_mode          = {l.dm_mode}");
                    sb.AppendLine($"    dm_version_index = {l.dm_version_index}");
                    break;
                }

                case 255:
                {
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
