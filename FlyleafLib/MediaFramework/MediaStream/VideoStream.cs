using System.Runtime.InteropServices;

using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaStream;

public unsafe class VideoStream : StreamBase
{
    public AspectRatio                  AspectRatio         { get; set; }
    public ColorRange                   ColorRange          { get; set; }
    public ColorSpace                   ColorSpace          { get; set; }
    public AVColorTransferCharacteristic
                                        ColorTransfer       { get; set; }
    public double                       Rotation            { get; set; }
    public double                       FPS                 { get; set; }
    public long                         FrameDuration       { get ;set; }
    public uint                         Height              { get; set; }
    public bool                         IsRGB               { get; set; }
    public AVComponentDescriptor[]      PixelComps          { get; set; }
    public int                          PixelComp0Depth     { get; set; }
    public AVPixelFormat                PixelFormat         { get; set; }
    public AVPixFmtDescriptor*          PixelFormatDesc     { get; set; }
    public string                       PixelFormatStr      { get; set; }
    public int                          PixelPlanes         { get; set; }
    public bool                         PixelSameDepth      { get; set; }
    public bool                         PixelInterleaved    { get; set; }
    public int                          TotalFrames         { get; set; }
    public uint                         Width               { get; set; }
    public bool                         FixTimestamps       { get; set; } // TBR: For formats such as h264/hevc that have no or invalid pts values

    public override string GetDump()
        => $"[{Type} #{StreamIndex}] {Codec} {PixelFormatStr} {Width}x{Height} @ {FPS:#.###} | [Color: {ColorSpace}] [BR: {BitRate}] | {Utils.TicksToTime((long)(AVStream->start_time * Timebase))}/{Utils.TicksToTime((long)(AVStream->duration * Timebase))} | {Utils.TicksToTime(StartTime)}/{Utils.TicksToTime(Duration)}";

    public VideoStream() { }
    public VideoStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
    {
        Demuxer = demuxer;
        AVStream = st;
        Refresh();
    }

    public void Refresh(AVPixelFormat format = AVPixelFormat.None, AVFrame* frame = null)
    {
        base.Refresh();

        PixelFormat     = format == AVPixelFormat.None ? (AVPixelFormat)AVStream->codecpar->format : format;
        PixelFormatStr  = PixelFormat.ToString().Replace("AV_PIX_FMT_","").ToLower();
        Width           = (uint)AVStream->codecpar->width;
        Height          = (uint)AVStream->codecpar->height;

        // TBR: Maybe required also for input formats with AVFMT_NOTIMESTAMPS (and audio/subs)
        // Possible FFmpeg.Autogen bug with Demuxer.FormatContext->iformat->flags (should be uint?) does not contain AVFMT_NOTIMESTAMPS (256 instead of 384)
        if (Demuxer.Name == "h264" || Demuxer.Name == "hevc")
        {
            FixTimestamps = true;

            if (Demuxer.Config.ForceFPS > 0)
                FPS = Demuxer.Config.ForceFPS;
            else
                FPS = av_q2d(av_guess_frame_rate(Demuxer.FormatContext, AVStream, frame));

            if (FPS == 0)
                FPS = 25;
        }
        else
        {
            FixTimestamps = false;
            FPS  = av_q2d(av_guess_frame_rate(Demuxer.FormatContext, AVStream, frame));
        }

        FrameDuration   = FPS > 0 ? (long) (10000000 / FPS) : 0;
        TotalFrames     = AVStream->duration > 0 && FrameDuration > 0 ? (int) (AVStream->duration * Timebase / FrameDuration) : (FrameDuration > 0 ? (int) (Demuxer.Duration / FrameDuration) : 0);

        int x, y;
        AVRational sar = av_guess_sample_aspect_ratio(null, AVStream, null);
        if (av_cmp_q(sar, av_make_q(0, 1)) <= 0)
            sar = av_make_q(1, 1);

        av_reduce(&x, &y, Width  * sar.Num, Height * sar.Den, 1024 * 1024);
        AspectRatio = new AspectRatio(x, y);

        if (PixelFormat != AVPixelFormat.None)
        {
            ColorRange = AVStream->codecpar->color_range == AVColorRange.Jpeg ? ColorRange.Full : ColorRange.Limited;

            if (AVStream->codecpar->color_space == AVColorSpace.Bt470bg)
                ColorSpace = ColorSpace.BT601;
            else if (AVStream->codecpar->color_space == AVColorSpace.Bt709)
                ColorSpace = ColorSpace.BT709;
            else ColorSpace = AVStream->codecpar->color_space == AVColorSpace.Bt2020Cl || AVStream->codecpar->color_space == AVColorSpace.Bt2020Ncl
                ? ColorSpace.BT2020
                : Height > 576 ? ColorSpace.BT709 : ColorSpace.BT601;

            // This causes issues
            //if (AVStream->codecpar->color_space == AVColorSpace.AVCOL_SPC_UNSPECIFIED && AVStream->codecpar->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED && Height > 1080)
            //{   // TBR: Handle Dolphy Vision?
            //    ColorSpace = ColorSpace.BT2020;
            //    ColorTransfer = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
            //}
            //else
            ColorTransfer = AVStream->codecpar->color_trc;

            // We get rotation from frame side data only from 1st frame in case of exif orientation (mainly for jpeg) - TBR if required to check for each frame
            AVFrameSideData* frameSideData;
            AVPacketSideData* pktSideData;
            double rotation = 0;
            if (frame != null && (frameSideData = av_frame_get_side_data(frame, AVFrameSideDataType.Displaymatrix)) != null && frameSideData->data != null)
                rotation = -Math.Round(av_display_rotation_get((int*)frameSideData->data)); //int_array9 displayMatrix = Marshal.PtrToStructure<int_array9>((nint)frameSideData->data); TBR: NaN why?
            else if ((pktSideData = av_packet_side_data_get(AVStream->codecpar->coded_side_data, AVStream->codecpar->nb_coded_side_data, AVPacketSideDataType.Displaymatrix)) != null && pktSideData->data != null)
                rotation = -Math.Round(av_display_rotation_get((int*)pktSideData->data));

            Rotation = rotation - (360*Math.Floor(rotation/360 + 0.9/360));

            PixelFormatDesc = av_pix_fmt_desc_get(PixelFormat);
            var comps       = PixelFormatDesc->comp.ToArray();
            PixelComps      = new AVComponentDescriptor[PixelFormatDesc->nb_components];
            for (int i=0; i<PixelComps.Length; i++)
                PixelComps[i] = comps[i];

            PixelInterleaved= PixelFormatDesc->log2_chroma_w != PixelFormatDesc->log2_chroma_h;
            IsRGB           = (PixelFormatDesc->flags & PixFmtFlags.Rgb) != 0;

            PixelSameDepth  = true;
            PixelPlanes     = 0;
            if (PixelComps.Length > 0)
            {
                PixelComp0Depth = PixelComps[0].depth;
                int prevBit     = PixelComp0Depth;
                for (int i=0; i<PixelComps.Length; i++)
                {
                    if (PixelComps[i].plane > PixelPlanes)
                        PixelPlanes = PixelComps[i].plane;

                    if (prevBit != PixelComps[i].depth)
                        PixelSameDepth = false;
                }

                PixelPlanes++;
            }
        }
    }
}
