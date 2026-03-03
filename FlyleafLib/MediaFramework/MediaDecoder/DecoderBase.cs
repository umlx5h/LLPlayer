using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public abstract unsafe class DecoderBase : RunThreadBase
{
    public MediaType                Type            { get; protected set; }

    public bool                     OnVideoDemuxer  => demuxer?.Type == MediaType.Video;
    public Demuxer                  Demuxer         => demuxer;
    public StreamBase               Stream          { get; protected set; }
    public AVCodecContext*          CodecCtx        => codecCtx;
    public int                      Width           => codecCtx != null ? codecCtx->width : 0;  // for subtitle
    public int                      Height          => codecCtx != null ? codecCtx->height : 0; // for subtitle

    public Action<DecoderBase>      CodecChanged    { get; set; }
    public Config                   Config          { get; protected set; }
    public double                   Speed           { get => speed; set { if (Disposed) { speed = value; return; } if (speed != value) OnSpeedChanged(value); } }
    protected double speed = 1, oldSpeed = 1;
    protected virtual void OnSpeedChanged(double value) { }

    internal bool               codecChanged;
    internal bool               filledFromCodec;
    protected AVFrame*          frame;
    protected AVCodecContext*   codecCtx;
    internal  object            lockCodecCtx    = new();

    protected Demuxer           demuxer;

    public DecoderBase(Config config, int uniqueId = -1) : base(uniqueId)
    {
        Config = config;

        if (this is VideoDecoder)
            Type = MediaType.Video;
        else if (this is AudioDecoder)
            Type = MediaType.Audio;
        else if (this is SubtitlesDecoder)
            Type = MediaType.Subs;
        else if (this is DataDecoder)
            Type = MediaType.Data;

        threadName = $"Decoder: {Type, 5}";
    }

    public bool Open(StreamBase stream)
    {
        lock (lockActions)
        {
            var prevStream = Stream;
            Dispose();
            frame = av_frame_alloc();   // TBR: Consider different full Dispose (including also the renderer?)* | 1) to avoid re-allocating -just unref- 2) we might forget it somewhere
            Status = Status.Opening;
            return Open2(stream, prevStream);
        }
    }
    protected bool Open2(StreamBase stream, StreamBase prevStream, bool openStream = true)
    {
        lock (stream.Demuxer.lockActions)
        {
            if (stream == null || stream.Demuxer.Interrupter.ForceInterrupt == 1 || stream.Demuxer.Disposed)
            {
                Log.Debug("Cancelled");
                return false;
            }

            Disposed= false;
            Stream  = stream;
            demuxer = stream.Demuxer;

            if (!Setup())
            {
                Dispose(true);
                return false;
            }

            if (openStream)
            {
                if (prevStream != null)
                {
                    if (prevStream.Demuxer.Type == stream.Demuxer.Type)
                        stream.Demuxer.SwitchStream(stream);
                    else if (!prevStream.Demuxer.Disposed)
                    {
                        if (prevStream.Demuxer.Type == MediaType.Video)
                            prevStream.Demuxer.DisableStream(prevStream);
                        else if (prevStream.Demuxer.Type == MediaType.Audio || prevStream.Demuxer.Type == MediaType.Subs)
                            prevStream.Demuxer.Dispose();

                        stream.Demuxer.EnableStream(stream);
                    }
                }
                else
                    stream.Demuxer.EnableStream(stream);

                Status = Status.Stopped;
            }

            return true;
        }
    }
    protected abstract bool Setup();

    public void Dispose(bool closeStream = false)
    {
        if (Disposed)
            return;

        lock (lockActions)
        {
            if (Disposed)
                return;

            Stop();
            DisposeInternal();

            if (closeStream && Stream != null && !Stream.Demuxer.Disposed)
            {
                if (Stream.Demuxer.Type == MediaType.Video)
                    Stream.Demuxer.DisableStream(Stream);
                else
                    Stream.Demuxer.Dispose();
            }

            if (frame != null)
                fixed (AVFrame** ptr = &frame)
                    av_frame_free(ptr);

            if (codecCtx != null)
                fixed (AVCodecContext** ptr = &codecCtx)
                    avcodec_free_context(ptr);

            codecCtx        = null;
            demuxer         = null;
            Stream          = null;
            Status          = Status.Stopped;
            Disposed        = true;

            if (CanDebug) Log.Debug("Disposed");
        }
    }
    protected abstract void DisposeInternal();
}
