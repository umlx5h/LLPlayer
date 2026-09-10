using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaStream;

public unsafe class AudioStream : StreamBase
{
    public int              Bits                { get; set; }
    public int              Channels            { get; set; }
    public AVChannelLayout  ChannelLayout       { get; set; }
    public string           ChannelLayoutStr    { get; set; }
    public AVSampleFormat   SampleFormat        { get; set; }
    public string           SampleFormatStr     { get; set; }
    public int              SampleRate          { get; set; }
    public AVCodecID        CodecIDOrig         { get; set; }

    public AudioStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
        => Type = MediaType.Audio;

    public override void Initialize()
    {
        // https://trac.ffmpeg.org/ticket/7321
        CodecIDOrig = CodecID;
        if (CodecID == AVCodecID.Mp2 && (SampleFormat == AVSampleFormat.Fltp || SampleFormat == AVSampleFormat.Flt))
            CodecID = AVCodecID.Mp3; // OR? st->codecpar->format = (int) AVSampleFormat.AV_SAMPLE_FMT_S16P;

        Bits            = cp->bits_per_coded_sample;
        SampleFormat    = (AVSampleFormat)cp->format;
        SampleFormatStr = LowerCaseFirstChar(SampleFormat.ToString());
        SampleRate      = cp->sample_rate;

        if (cp->ch_layout.order == AVChannelOrder.Unspec && cp->ch_layout.nb_channels > 0)
            av_channel_layout_default(&cp->ch_layout, cp->ch_layout.nb_channels);

        ChannelLayout   = cp->ch_layout;
        Channels        = ChannelLayout.nb_channels;
        ChannelLayoutStr= GetChannelLayoutStr(ChannelLayout);
    }

    public void Refresh(AudioDecoder decoder, AVFrame* frame)
    {
        var codecCtx = decoder.CodecCtx;

        ReUpdate();
        
        if (codecCtx->bits_per_coded_sample > 0)
            Bits = codecCtx->bits_per_coded_sample;

        if (codecCtx->bit_rate > 0)
            BitRate = codecCtx->bit_rate; // for logging only

        if (frame->format != (int)AVSampleFormat.None)
        {
            SampleFormat = (AVSampleFormat)frame->format;
            SampleFormatStr = LowerCaseFirstChar(SampleFormat.ToString());
        }

        if (frame->sample_rate > 0)
            SampleRate = frame->sample_rate;
        else if (codecCtx->sample_rate > 0)
            SampleRate = codecCtx->sample_rate;

        if (frame->ch_layout.nb_channels > 0)
        {
            if (frame->ch_layout.order == AVChannelOrder.Unspec)
                av_channel_layout_default(&frame->ch_layout, frame->ch_layout.nb_channels);

            ChannelLayout   = frame->ch_layout;
            Channels        = ChannelLayout.nb_channels;
            ChannelLayoutStr= GetChannelLayoutStr(ChannelLayout);
        }
        else if (codecCtx->ch_layout.nb_channels > 0)
        {
            if (codecCtx->ch_layout.order == AVChannelOrder.Unspec)
                av_channel_layout_default(&codecCtx->ch_layout, codecCtx->ch_layout.nb_channels);

            ChannelLayout   = codecCtx->ch_layout;
            Channels        = ChannelLayout.nb_channels;
            ChannelLayoutStr= GetChannelLayoutStr(ChannelLayout);
        }

        if (CanDebug)
            Demuxer.Log.Debug($"Stream Info (Filled)\r\n{GetDump()}");
    }
}
