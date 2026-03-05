using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    /* TODO
     * 1) Try to sync filters between Flyleaf and D3D11 video processors so we will not have to reset on change
     * 2) Filter default values will change when the device/adapter is changed
     */

    static Dictionary<long, VideoProcessorCapsCache> VideoProcessorsCapsCache = [];
    VideoProcessorCapsCache curVPCC;

    internal static VideoProcessorFilter ConvertFromVideoProcessorFilterCaps(VideoProcessorFilterCaps filter)
    {
        return filter switch
        {
            VideoProcessorFilterCaps.Brightness         => VideoProcessorFilter.Brightness,
            VideoProcessorFilterCaps.Contrast           => VideoProcessorFilter.Contrast,
            VideoProcessorFilterCaps.Hue                => VideoProcessorFilter.Hue,
            VideoProcessorFilterCaps.Saturation         => VideoProcessorFilter.Saturation,
            VideoProcessorFilterCaps.EdgeEnhancement    => VideoProcessorFilter.EdgeEnhancement,
            VideoProcessorFilterCaps.NoiseReduction     => VideoProcessorFilter.NoiseReduction,
            VideoProcessorFilterCaps.AnamorphicScaling  => VideoProcessorFilter.AnamorphicScaling,
            VideoProcessorFilterCaps.StereoAdjustment   => VideoProcessorFilter.StereoAdjustment,
            _ => VideoProcessorFilter.StereoAdjustment,
        };
    }
    internal static VideoProcessorFilterCaps ConvertFromVideoProcessorFilter(VideoProcessorFilter filter)
    {
        return filter switch
        {
            VideoProcessorFilter.Brightness             => VideoProcessorFilterCaps.Brightness,
            VideoProcessorFilter.Contrast               => VideoProcessorFilterCaps.Contrast,
            VideoProcessorFilter.Hue                    => VideoProcessorFilterCaps.Hue,
            VideoProcessorFilter.Saturation             => VideoProcessorFilterCaps.Saturation,
            VideoProcessorFilter.EdgeEnhancement        => VideoProcessorFilterCaps.EdgeEnhancement,
            VideoProcessorFilter.NoiseReduction         => VideoProcessorFilterCaps.NoiseReduction,
            VideoProcessorFilter.AnamorphicScaling      => VideoProcessorFilterCaps.AnamorphicScaling,
            VideoProcessorFilter.StereoAdjustment       => VideoProcessorFilterCaps.StereoAdjustment,
            _ => VideoProcessorFilterCaps.StereoAdjustment,
        };
    }
    internal static VideoFilterLocal ConvertFromVideoProcessorFilterRange(VideoProcessorFilterRange filter) => new()
    {
        Minimum     = filter.Minimum,
        Maximum     = filter.Maximum,
        Default     = filter.Default,
        Step        = filter.Multiplier
    };

    VideoColor                          D3D11VPBackgroundColor;
    ID3D11VideoDevice1                  vd1;
    ID3D11VideoProcessor                vp;
    ID3D11VideoContext                  vc;
    ID3D11VideoProcessorEnumerator      vpe;
    ID3D11VideoProcessorInputView       vpiv;
    ID3D11VideoProcessorOutputView      vpov;

    VideoProcessorStream[]              vpsa    = [new() { Enable = true }];
    VideoProcessorContentDescription    vpcd    = new()
        {
            Usage           = VideoUsage.PlaybackNormal,
            InputFrameFormat= VideoFrameFormat.Progressive,

            InputFrameRate  = new(1, 1),
            OutputFrameRate = new(1, 1),
        };
    VideoProcessorOutputViewDescription vpovd   = new() { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
    VideoProcessorInputViewDescription  vpivd   = new()
        {
            FourCC          = 0,
            ViewDimension   = VideoProcessorInputViewDimension.Texture2D,
            Texture2D       = new() { MipSlice = 0, ArraySlice = 0 }
        };
    VideoProcessorColorSpace            inputColorSpace;
    VideoProcessorColorSpace            outputColorSpace;

    uint actualRotation;
    bool actualHFlip, actualVFlip;

    bool InitializeVideoProcessor()
    {
        try
        {
            vpcd.InputWidth     = 1;
            vpcd.InputHeight    = 1;
            vpcd.OutputWidth    = vpcd.InputWidth;
            vpcd.OutputHeight   = vpcd.InputHeight;

            outputColorSpace = new VideoProcessorColorSpace()
            {
                Usage           = 0,
                RGB_Range       = 0,
                YCbCr_Matrix    = 1,
                YCbCr_xvYCC     = 0,
                Nominal_Range   = 2
            };

            lock (VideoProcessorsCapsCache)
            {
                if (VideoProcessorsCapsCache.TryGetValue(gpuAdapter.Luid, out curVPCC))
                {
                    while (curVPCC.Wait)
                        Thread.Sleep(10);

                    if (curVPCC.Failed)
                        return false;

                    vd1 = Device. QueryInterface<ID3D11VideoDevice1>();
                    vc  = context.QueryInterface<ID3D11VideoContext1>();

                    vd1.CreateVideoProcessorEnumerator(ref vpcd, out vpe);

                    if (vpe == null)
                        return false;

                    vd1.CreateVideoProcessor(vpe, (uint)curVPCC.TypeIndex, out vp);

                    return true;
                }

                curVPCC = new();
                VideoProcessorsCapsCache.Add(gpuAdapter.Luid, curVPCC);
            }

            vd1 = Device. QueryInterface<ID3D11VideoDevice1>();
            vc  = context.QueryInterface<ID3D11VideoContext>();

            vd1.CreateVideoProcessorEnumerator(ref vpcd, out vpe);

            if (vpe == null || FeatureLevel < Vortice.Direct3D.FeatureLevel.Level_10_0)
                return false;

            var vpe1    = vpe.QueryInterface<ID3D11VideoProcessorEnumerator1>();
            var vpCaps  = vpe.VideoProcessorCaps;
            string dump = "";

            if (CanDebug)
            {
                var hlg     = vpe1.CheckVideoProcessorFormatConversion(Format.P010, ColorSpaceType.YcbcrStudioGhlgTopLeftP2020,  BGRA_OR_RGBA, ColorSpaceType.RgbFullG22NoneP709);
                var hdr10   = vpe1.CheckVideoProcessorFormatConversion(Format.P010, ColorSpaceType.YcbcrStudioG2084TopLeftP2020, BGRA_OR_RGBA, ColorSpaceType.RgbStudioG2084NoneP2020);

                dump += $"=====================================================\r\n";
                dump += $"MaxInputStreams           {vpCaps.MaxInputStreams}\r\n";
                dump += $"MaxStreamStates           {vpCaps.MaxStreamStates}\r\n";
                dump += $"HDR10 Limited             {(hlg   ? "yes" : "no")}\r\n";
                dump += $"HLG                       {(hdr10 ? "yes" : "no")}\r\n";

                dump += $"\n[Video Processor Device Caps]\r\n";
                foreach (VideoProcessorDeviceCaps cap in Enum.GetValues(typeof(VideoProcessorDeviceCaps)))
                    dump += $"{cap,-25} {((vpCaps.DeviceCaps & cap) != 0 ? "yes" : "no")}\r\n";

                dump += $"\n[Video Processor Feature Caps]\r\n";
                foreach (VideoProcessorFeatureCaps cap in Enum.GetValues(typeof(VideoProcessorFeatureCaps)))
                    dump += $"{cap,-25} {((vpCaps.FeatureCaps & cap) != 0 ? "yes" : "no")}\r\n";

                dump += $"\n[Video Processor Stereo Caps]\r\n";
                foreach (VideoProcessorStereoCaps cap in Enum.GetValues(typeof(VideoProcessorStereoCaps)))
                    dump += $"{cap,-25} {((vpCaps.StereoCaps & cap) != 0 ? "yes" : "no")}\r\n";

                dump += $"\n[Video Processor Input Format Caps]\r\n";
                foreach (VideoProcessorFormatCaps cap in Enum.GetValues(typeof(VideoProcessorFormatCaps)))
                    dump += $"{cap,-25} {((vpCaps.InputFormatCaps & cap) != 0 ? "yes" : "no")}\r\n";

                dump += $"\n[Video Processor Filter Caps]\r\n";
            }

            foreach (VideoProcessorFilterCaps filter in Enum.GetValues(typeof(VideoProcessorFilterCaps)))
                if ((vpCaps.FilterCaps & filter) != 0)
                {
                    vpe1.GetVideoProcessorFilterRange(ConvertFromVideoProcessorFilterCaps(filter), out var range);
                    if (CanDebug) dump += $"{filter,-25} [{range.Minimum,6} - {range.Maximum,4}] | x{range.Multiplier,4} | *{range.Default}\r\n";
                    var vf = ConvertFromVideoProcessorFilterRange(range);
                    vf.Filter = (VideoFilters)filter;
                    curVPCC.Filters.Add((VideoFilters)filter, vf);
                }
                else if (CanDebug)
                    dump += $"{filter,-25} no\r\n";

            if (CanDebug)
            {
                dump += $"\n[Video Processor Auto Stream Caps]\r\n";
                foreach (VideoProcessorAutoStreamCaps cap in Enum.GetValues(typeof(VideoProcessorAutoStreamCaps)))
                    dump += $"{cap,-25} {((vpCaps.AutoStreamCaps & cap) != 0 ? "yes" : "no")}\r\n";
            }

            uint typeIndex = 0;
            VideoProcessorRateConversionCaps rcCap = new();
            for (uint i = 0; i < vpCaps.RateConversionCapsCount; i++)
            {
                vpe.GetVideoProcessorRateConversionCaps(i, out rcCap);
                VideoProcessorProcessorCaps pCaps = (VideoProcessorProcessorCaps) rcCap.ProcessorCaps;

                if (CanDebug)
                {
                    dump += $"\n[Video Processor Rate Conversion Caps #{i}]\r\n";

                    dump += $"\n\t[Video Processor Rate Conversion Caps]\r\n";
                    var fields = typeof(VideoProcessorRateConversionCaps).GetFields();
                    foreach (var field in fields)
                        dump += $"\t{field.Name,-35} {field.GetValue(rcCap)}\r\n";

                    dump += $"\n\t[Video Processor Processor Caps]\r\n";
                    foreach (VideoProcessorProcessorCaps cap in Enum.GetValues(typeof(VideoProcessorProcessorCaps)))
                        dump += $"\t{cap,-35} {(((VideoProcessorProcessorCaps)rcCap.ProcessorCaps & cap) != 0 ? "yes" : "no")}\r\n";
                }

                typeIndex = i;

                if (((VideoProcessorProcessorCaps)rcCap.ProcessorCaps & VideoProcessorProcessorCaps.DeinterlaceBob) != 0)
                    break; // TBR: When we add past/future frames support
            }
            vpe1.Dispose();

            if (CanDebug) Log.Debug($"D3D11 Video Processor\r\n{dump}");

            curVPCC.TypeIndex = (int)typeIndex;
            vd1.CreateVideoProcessor(vpe, typeIndex, out vp);
            if (vp == null)
                return false;

            curVPCC.Failed = false;
            Log.Info($"D3D11 Video Processor Initialized (Rate Caps #{typeIndex})");

            return true;

        }
        catch { return false; }
        finally
        {
            curVPCC.Wait = false;

            if (curVPCC.Failed)
            {
                Log.Error($"D3D11 Video Processor Initialization Failed");
                DisposeVideoProcessor();
            }
            else
            {
                if (SuperResolution)
                {
                    SuperResolution = false;
                    RaiseUI(nameof(SuperResolution));
                }

                UpdateBackgroundColor();
                vc.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
                vc.VideoProcessorSetStreamFrameFormat(vp, 0, FieldType);
            }

            lock (Config.Video.lockFilters)
                lock (Config.Video.D3Filters)
                    SetupFilters();
        }
    }

    void DisposeVideoProcessor()
    {
        vpiv?.Dispose();
        vpov?.Dispose();
        vp?.  Dispose();
        vpe?. Dispose();
        vc?.  Dispose();
        vd1?. Dispose();

        vc = null;
    }

    VideoProcessors GetVP()
    {
        VideoProcessors getVP;

        if (vc == null || !VideoDecoder.VideoAccelerated) // D3D11VP not supported
            getVP = VideoProcessors.Flyleaf;
        else if (Config.Video.VideoProcessor == VideoProcessors.Auto)
        {
            if (VideoStream.ColorSpace == ColorSpace.Bt2020 || actualHFlip || actualVFlip)
                getVP = VideoProcessors.Flyleaf;
            else
                getVP = VideoProcessors.D3D11; // Deinterlace | Super Resolution | Extra Filters | Less power consumption
        }
        else
            getVP = Config.Video.VideoProcessor;

        if (getVP == VideoProcessors.D3D11)
        {
            vpsa[0].OutputIndex = vpsa[0].InputFrameOrField = 0;
            FieldType = Config.Video.DeInterlace == DeInterlace.Auto ? VideoStream.FieldOrder : (VideoFrameFormat)Config.Video.DeInterlace;
            vc.VideoProcessorSetStreamFrameFormat(vp, 0, FieldType);
        }
        else
            FieldType = VideoFrameFormat.Progressive;

        return getVP;
    }

    internal void UpdateBackgroundColor()
    {
        D3D11VPBackgroundColor.Rgba.R = Scale(Config.Video.BackgroundColor.R, 0, 255, 0, 100) / 100.0f;
        D3D11VPBackgroundColor.Rgba.G = Scale(Config.Video.BackgroundColor.G, 0, 255, 0, 100) / 100.0f;
        D3D11VPBackgroundColor.Rgba.B = Scale(Config.Video.BackgroundColor.B, 0, 255, 0, 100) / 100.0f;

        vc?.VideoProcessorSetOutputBackgroundColor(vp, false, D3D11VPBackgroundColor);

        RenderRequest();
    }

    internal void UpdateDeinterlace()
    {
        lock (lockDevice)
        {
            if (Disposed || VideoStream == null)
                return;

            if (GetVP() != VideoProcessor)
                ConfigPlanes();
            else
                RenderRequest();
        }
    }

    internal void UpdateHDRtoSDR(bool updateResource = true)
    {
        lock (lockDevice)
            if (!Disposed)
                UpdateHDRtoSDRUnSafe(updateResource);
    }
    internal void UpdateHDRtoSDRUnSafe(bool updateResource = true)
    {
        psBufferData.tonemap = Config.Video.HDRtoSDRMethod;

        switch (psBufferData.tonemap)
        {
            case HDRtoSDRMethod.Hable:
                psBufferData.hdrtone = 10_000f / Config.Video.SDRDisplayNits;
                break;

            case HDRtoSDRMethod.Reinhard:
                psBufferData.hdrtone = (10_000f / Config.Video.SDRDisplayNits) / 2f;
                break;

            case HDRtoSDRMethod.Aces:
                psBufferData.hdrtone = (10_000f / Config.Video.SDRDisplayNits) / 7f;
                break;
        }

        if (updateResource)
        {
            context.UpdateSubresource(psBufferData, psBuffer);
            RenderRequest();
        }
    }

    void UpdateRotation(uint angle, bool refresh = true)
    {
        lock (lockDevice)
            if (!Disposed)
                UpdateRotationUnSafe(angle, refresh);
    }
    void UpdateRotationUnSafe(uint angle, bool refresh = true)
    {   // NOTE: H/V Flip not supported by D3D11VP (TODO: Switch VP if required (Auto)?)
        _RotationAngle      = angle;
        uint newRotation    = _RotationAngle;
        if (VideoStream != null)
            newRotation    += (uint)VideoStream.Rotation;
        newRotation        %= 360;

        bool newVflip       = hasLinesizeVFlip ^ _VFlip;
        bool hvFlipChanged  = actualHFlip != _HFlip || actualVFlip != newVflip;

        if (Disposed || (actualRotation == newRotation && !hvFlipChanged))
            return;

        actualRotation  = newRotation;
        actualVFlip     = newVflip;
        actualHFlip     = _HFlip;

        if (actualRotation < 45 || actualRotation == 360)
            _d3d11vpRotation = VideoProcessorRotation.Identity;
        else if (actualRotation < 135)
            _d3d11vpRotation = VideoProcessorRotation.Rotation90;
        else if (actualRotation < 225)
            _d3d11vpRotation = VideoProcessorRotation.Rotation180;
        else if (actualRotation < 360)
            _d3d11vpRotation = VideoProcessorRotation.Rotation270;

        vsBufferData.mat = Matrix4x4.CreateFromYawPitchRoll(0.0f, 0.0f, (float) (Math.PI / 180 * actualRotation));

        if (actualHFlip || actualVFlip)
        {
            vsBufferData.mat *= Matrix4x4.CreateScale(actualHFlip ? -1 : 1, actualVFlip ? -1 : 1, 1);
            if (hvFlipChanged)
            {
                // Renders both sides required for H-V Flip - TBR: consider for performance changing the vertex buffer / input layout instead?
                rasterizerState?.Dispose();
                rasterizerState = Device.CreateRasterizerState(new(CullMode.None, FillMode.Solid));
                context.RSSetState(rasterizerState);
            }
        }
        else if (hvFlipChanged)
        {
            // Removes back rendering for better performance
            rasterizerState?.Dispose();
            rasterizerState = Device.CreateRasterizerState(new(CullMode.Back, FillMode.Solid));
            context.RSSetState(rasterizerState);
        }

        context.UpdateSubresource(vsBufferData, vsBuffer);
        vc?.VideoProcessorSetStreamRotation(vp, 0, true, _d3d11vpRotation);

        UpdateAspectRatioUnSafe(refresh);
    }

    internal void UpdateAspectRatio(bool refresh = true)
    {
        lock (lockDevice)
            if (!Disposed)
                UpdateAspectRatioUnSafe(refresh);
    }
    internal void UpdateAspectRatioUnSafe(bool refresh = true)
    {   // TBR: Fix AspectRatio generally* Separate Enum + CustomValue (respect SAR, clarify Fit/Fill/Keep/Stretch/Original/Custom ...)
        if (Config.Video.AspectRatio == AspectRatio.Keep)
        {
            curRatio = keepRatio;
            if (actualRotation == 90 || actualRotation == 270)
                curRatio = 1 / curRatio;

            player?.Host?.Player_RatioChanged(curRatio); // return handled and avoid SetViewport?*
        }
        else if (Config.Video.AspectRatio == AspectRatio.Fill)
        {
            curRatio = fillRatio;
            if (actualRotation == 90 || actualRotation == 270)
                curRatio = 1 / curRatio;
        }
        else // No SAR / Rotation respect for customs?
            curRatio = Config.Video.AspectRatio == AspectRatio.Custom ? Config.Video.CustomAspectRatio.Value : Config.Video.AspectRatio.Value;

        player?.Host?.Player_RatioChanged(curRatio); // return handled and avoid SetViewport?*

        SetViewport(refresh);
    }

    internal void UpdateCropping(bool refresh = true)
    {
        lock (lockDevice)
            if (!Disposed && VideoStream != null)
                UpdateCroppingUnSafe(refresh);
    }
    internal void UpdateCroppingUnSafe(bool refresh = true)
    {
        /* TODO (SW)
         *
         * 1) Visible vs Padded
         *  When we fix texture arrays and texture width/height to use padded properly,
         *  we will need to ensure cropping pixels are in the same logic with vertex shader
         *
         * 2) Chroma Location + Crop Calculation/Adjustments
         *  PSInput additional uv coords (both vertex/pixel shaders) and use new coords for sampling
         *      float2 Texture  : TEXCOORD;
         *
         *  e.g. UV Chroma (Semi-Planar) Crop +- (0.5f / (W|H / 2f))?
         */

        cropRect = VideoStream.cropRect;

        if (Config.Video.HasUserCrop)
        {
            cropRect.Top    += Config.Video._Crop.Top;
            cropRect.Left   += Config.Video._Crop.Left;
            cropRect.Right  += Config.Video._Crop.Right;
            cropRect.Bottom += Config.Video._Crop.Bottom;
        }

        vsBufferData.cropRegion = new()
        {
            X = cropRect.Left / (float)textWidth,
            Y = cropRect.Top  / (float)textHeight,
            Z = (textWidth  - cropRect.Right)  / (float)textWidth, //1.0f - (right  / (float)textWidth),
            W = (textHeight - cropRect.Bottom) / (float)textHeight //1.0f - (bottom / (float)textHeight)
        };

        context.UpdateSubresource(vsBufferData, vsBuffer);

        VisibleWidth    = textWidth  - (cropRect.Left + cropRect.Right);
        VisibleHeight   = textHeight - (cropRect.Top  + cropRect.Bottom);

        var alphaPos = Config.Video._SplitFrameAlphaPosition;
        if (alphaPos != SplitFrameAlphaPosition.None)
        {
            if      (alphaPos == SplitFrameAlphaPosition.Left  || alphaPos == SplitFrameAlphaPosition.Right)
                VisibleWidth /= 2;
            else if (alphaPos == SplitFrameAlphaPosition.Top   || alphaPos == SplitFrameAlphaPosition.Bottom)
                VisibleHeight /= 2;
        }

        int x, y;
        _ = av_reduce(&x, &y, VisibleWidth * VideoStream.SAR.Num, VisibleHeight * VideoStream.SAR.Den, 1024 * 1024);
        DAR = new(x, y);
        keepRatio = DAR.Value;

        player?.Video.SetUISize((int)VisibleWidth, (int)VisibleHeight, DAR);

        if (Config.Video.AspectRatio == AspectRatio.Keep)
        {
            curRatio = actualRotation == 90 || actualRotation == 270 ? 1 / keepRatio : keepRatio;
            player?.Host?.Player_RatioChanged(curRatio);
        }

        SetViewport(refresh);
    }

    internal void UpdateVideoProcessor()
    {
        if (Config.Video.VideoProcessor == videoProcessor || (Config.Video.VideoProcessor == VideoProcessors.D3D11 && vc == null))
            return;

        ConfigPlanes();
        RenderRequest();
    }

    #region Super Resolution
    public bool SuperResolution { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    struct SuperResNvidia(bool enable)
    {
        uint version = 0x1;
        uint method  = 0x2;
        uint enabled = enable ? 1u : 0u;
    }
    static readonly SuperResNvidia  SuperResEnabledNvidia   = new(true);
    static readonly SuperResNvidia  SuperResDisabledNvidia  = new(false);
    static readonly Guid            GUID_SUPERRES_NVIDIA    = Guid.Parse("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");

    [StructLayout(LayoutKind.Sequential)]
    struct SuperResIntel
    {
        public IntelFunction    function;
        public IntPtr           param;
    }
    enum IntelFunction : uint
    {
        kIntelVpeFnVersion  = 0x01,
        kIntelVpeFnMode     = 0x20,
		kIntelVpeFnScaling  = 0x37
    }
    static readonly Guid            GUID_SUPERRES_INTEL     = Guid.Parse("edd1d4b9-8659-4cbc-a4d6-9831a2163ac3");

    internal void UpdateSuperRes()
    {
        lock (lockDevice)
        {
            if (vc == null)
                return;

            if (!Config.Video.SuperResolution)
                DisableSuperRes();
            else
            {
                var view = GetViewport;
                if (((_RotationAngle ==  0 || _RotationAngle == 180) && view.Width > VisibleWidth  && view.Height > VisibleHeight) ||
                    ((_RotationAngle == 90 || _RotationAngle == 270) && view.Width > VisibleHeight && view.Height > VisibleWidth))
                    EnableSuperRes();
                else
                    DisableSuperRes();
            }
        }
    }

    void EnableSuperRes()
    {
        if (SuperResolution)
            return;

        SuperResolution = true;
        RaiseUI(nameof(SuperResolution));

        if (gpuAdapter.Vendor == GPUVendor.Nvidia)
            fixed (SuperResNvidia* ptr = &SuperResEnabledNvidia)
                vc.VideoProcessorSetStreamExtension(vp, 0, GUID_SUPERRES_NVIDIA, (uint)sizeof(SuperResNvidia), (nint)ptr);
        else if (gpuAdapter.Vendor == GPUVendor.Intel)
            UpdateSuperResIntel(true);
    }

    void DisableSuperRes()
    {
        if (!SuperResolution)
            return;

        SuperResolution = false;
        RaiseUI(nameof(SuperResolution));

        if (gpuAdapter.Vendor == GPUVendor.Nvidia)
            fixed (SuperResNvidia* ptr = &SuperResDisabledNvidia)
                vc.VideoProcessorSetStreamExtension(vp, 0, GUID_SUPERRES_NVIDIA, (uint)sizeof(SuperResNvidia), (nint)ptr);
        else if (gpuAdapter.Vendor == GPUVendor.Intel)
            UpdateSuperResIntel(false);
    }

    void UpdateSuperResIntel(bool enabled)
    {
        IntPtr          paramPtr    = Marshal.AllocHGlobal(sizeof(uint));
        SuperResIntel   intel       = new() { param = paramPtr };
        GCHandle        handle      = GCHandle.Alloc(intel, GCHandleType.Pinned);

        intel.function = IntelFunction.kIntelVpeFnVersion;
        Marshal.WriteInt32(paramPtr, 3); // kIntelVpeVersion3
        vc.VideoProcessorSetOutputExtension(vp,     GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        intel.function = IntelFunction.kIntelVpeFnMode;
        Marshal.WriteInt32(paramPtr, enabled ? 1 : 0); // kIntelVpeModePreproc : kIntelVpeModeNone
        vc.VideoProcessorSetOutputExtension(vp,     GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        intel.function = IntelFunction.kIntelVpeFnScaling;
        Marshal.WriteInt32(paramPtr, enabled ? 2 : 0); // kIntelVpeScalingSuperResolution : kIntelVpeScalingDefault
        vc.VideoProcessorSetStreamExtension(vp, 0,  GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        handle.Free();
        Marshal.FreeHGlobal(paramPtr);
    }
    #endregion
}

internal class VideoProcessorCapsCache
{
    public bool Wait        = true; // to avoid locking until init
    public bool Failed      = true;
    public int  TypeIndex   = -1;

    public Dictionary<VideoFilters, VideoFilterLocal> Filters { get; set; } = [];
}
