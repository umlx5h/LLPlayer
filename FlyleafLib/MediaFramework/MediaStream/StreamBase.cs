using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.MediaFramework.MediaStream;

public abstract unsafe class StreamBase : NotifyPropertyChanged
{
    public ExternalStream               ExternalStream      { get; set; }

    public Demuxer                      Demuxer             { get; internal set; }
    public AVStream*                    AVStream            { get; internal set; }
    internal playlist*                  HLSPlaylist         { get; set; }
    public int                          StreamIndex         { get; internal set; } = -1;
    public double                       Timebase            { get; internal set; }

    // TBR: To update Pop-up menu's (Player.Audio/Player.Video ... should inherit this?)
    public bool                         Enabled
    {
        get => _Enabled;
        internal set
        {
            Utils.UI(() =>
            {
                Set(ref _Enabled, value);

                Raise(nameof(EnabledPrimarySubtitle));
                Raise(nameof(EnabledSecondarySubtitle));
                Raise(nameof(SubtitlesStream.SelectedSubMethods));
            });
        }
    }

    bool _Enabled;

    public long                         BitRate             { get; internal set; }
    public Language                     Language            { get; internal set; }
    public string                       Title               { get; internal set; }
    public string                       Codec               { get; internal set; }

    public AVCodecID                    CodecID             { get; internal set; }
    public long                         StartTime           { get; internal set; }
    public long                         StartTimePts        { get; internal set; }
    public long                         Duration            { get; internal set; }
    public Dictionary<string, string>   Metadata            { get; internal set; } = [];
    public MediaType                    Type                { get; internal set; }

    protected AVCodecParameters* cp;

    #region Subtitles
    // TODO: L: Used for subtitle streams only, but defined in the base class
    public bool EnabledPrimarySubtitle => Enabled && this.GetSubEnabled(0);
    public bool EnabledSecondarySubtitle => Enabled && this.GetSubEnabled(1);
    #endregion

    public StreamBase(Demuxer demuxer, AVStream* st)
    {
        Demuxer     = demuxer;
        AVStream    = st;
        cp          = st->codecpar;
        BitRate     = cp->bit_rate;
        CodecID     = cp->codec_id;
        Codec       = avcodec_get_name(cp->codec_id);
        StreamIndex = AVStream->index;
        Timebase    = av_q2d(AVStream->time_base) * 10000.0 * 1000.0;

        if (AVStream->start_time != NoTs)
        {
            StartTimePts= AVStream->start_time;
            StartTime   = Demuxer.hlsCtx == null ? (long)(AVStream->start_time * Timebase) : Demuxer.StartTime;
        }
        else
        {
            StartTime   = Demuxer.StartTime;
            StartTimePts= av_rescale_q(StartTime/10, Engine.FFmpeg.AV_TIMEBASE_Q, AVStream->time_base);
        }

        UpdateMetadata();
        Initialize();
    }

    public abstract void Initialize();

    // Possible Fields Updated by FFmpeg (after open/decode frame)
    protected void ReUpdate()
    {
        if (AVStream->start_time != NoTs && Demuxer.hlsCtx == null)
        {
            StartTimePts= AVStream->start_time;
            StartTime   = (long)(StartTimePts * Timebase);
        }

        if (AVStream->duration != NoTs)
            Duration = (long)(AVStream->duration * Timebase);

        UpdateHLS();
    }

    // Demuxer Callback
    internal virtual void UpdateDuration()
    {
        Duration = AVStream->duration != NoTs ? (long)(AVStream->duration * Timebase) : Demuxer.Duration;
        UpdateHLS();
    }

    protected void UpdateHLS()
    {
        if (Demuxer.hlsCtx == null || HLSPlaylist != null)
            return;

        for (int i = 0; i < Demuxer.hlsCtx->n_playlists; i++)
        {
            playlist** playlists = Demuxer.hlsCtx->playlists;
            for (int l=0; l<playlists[i]->n_main_streams; l++)
                if (playlists[i]->main_streams[l]->index == StreamIndex)
                {
                    Demuxer.Log.Debug($"Stream #{StreamIndex} Found in playlist {i}");
                    HLSPlaylist = playlists[i];
                    break;
                }
        }
    }

    protected void UpdateMetadata()
    {
        Metadata.Clear();

        AVDictionaryEntry* b = null;
        while (true)
        {
            b = av_dict_get(AVStream->metadata, "", b, DictReadFlags.IgnoreSuffix);
            if (b == null) break;
            Metadata[BytePtrToStringUTF8(b->key)] = BytePtrToStringUTF8(b->value);
        }

        foreach (var kv in Metadata)
        {
            string keyLower = kv.Key.ToLower();

            if (Language == null && (keyLower == "language" || keyLower == "lang"))
                Language = Language.Get(kv.Value);
            else if (keyLower == "title")
                Title = kv.Value;
        }

        if (Language == null)
            Language = Language.Unknown;
    }

    public virtual string GetDump()
    {
        string dump = $"[{Type,-5} #{StreamIndex:D2}]";
        if (Language.OriginalInput != null)
            dump += $" ({Language.OriginalInput})";

        if (StartTime != NoTs || Duration != NoTs)
        {
            dump += "\r\n\t[Time	 ] ";
            dump += StartTimePts != NoTs ? $"{TicksToTime(StartTime)} ({StartTimePts})" : "-";
            dump += " / ";
            dump += AVStream->duration != NoTs ? $"{TicksToTime(Duration)} ({AVStream->duration})": "-";
            dump += $" | tb: {AVStream->time_base}";
        }

        string profile = null;
        var codecDescriptor = avcodec_descriptor_get(CodecID);
        if (codecDescriptor != null)
            profile = avcodec_profile_name(CodecID, cp->profile);
        dump += $"\r\n\t[Codec   ] {Codec}{(profile != null ? " | " + avcodec_profile_name(CodecID, cp->profile) : "")}";

        if (cp->codec_tag != 0)
            dump += $" ({GetFourCCString(cp->codec_tag)} / 0x{cp->codec_tag:X4})";

        if (BitRate > 0)
            dump += $", {(int)(BitRate / 1000)} kb/s";

        if (AVStream->disposition != DispositionFlags.None)
            dump += $" - ({GetFlagsAsString(AVStream->disposition)})";

        if (this is AudioStream audio)
            dump += $"\r\n\t[Format  ] {audio.SampleRate} Hz, {audio.ChannelLayoutStr}, {audio.SampleFormatStr}";
        else if (this is VideoStream video)
            dump += $"\r\n\t[Format  ] {video.PixelFormat} ({cp->color_primaries}, {video.ColorSpace}, {video.ColorTransfer}, {cp->chroma_location}, {video.ColorRange}, {video.FieldOrder}), {video.Width}x{video.Height} @ {DoubleToTimeMini(video.FPS)} fps [SAR: {video.SAR} DAR: {video.GetDAR()} Crop: {video.Cropping}]";

        if (Metadata.Count > 0)
            dump += $"\r\n{GetDumpMetadata(Metadata, "language")}";

        return dump;
    }
}

// Use to avoid nulls on broken streams (AVStreamToStream)
public unsafe class MiscStream : StreamBase
{
    public MiscStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
        => Type = MediaType.Data;

    public override void Initialize() { }
}
