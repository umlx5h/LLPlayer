using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Data;

using Vortice.Direct3D11;

using ID2D1DeviceContext = Vortice.Direct2D1.ID2D1DeviceContext;

using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaPlayer.Translation;
using FlyleafLib.MediaPlayer.Translation.Services;
using FlyleafLib.Plugins;

using Filter = FlyleafLib.MediaFramework.MediaDecoder.Filter;

namespace FlyleafLib;

/// <summary>
/// Player's configuration
/// </summary>
public class Config : NotifyPropertyChanged
{
    public static JsonSerializerOptions jsonOpts = new() { WriteIndented = true };

    public Config()
    {
        // Parse default plugin options to Config.Plugins (Creates instances until fix with statics in interfaces)
        foreach (var plugin in Engine.Plugins.Types.Values)
        {
            var tmpPlugin = PluginHandler.CreatePluginInstance(plugin);
            var defaultOptions = tmpPlugin.GetDefaultOptions();
            tmpPlugin.Dispose();

            if (defaultOptions == null || defaultOptions.Count == 0) continue;

            Plugins.Add(plugin.Name, []);
            foreach (var opt in defaultOptions)
                Plugins[plugin.Name].Add(opt.Key, opt.Value);
        }
        // save default plugin options for later
        defaultPlugins = Plugins;

        Player.config   = this;
        Demuxer.config  = this;
    }

    public Config(bool test) { }

    public Config Clone()
    {
        Config config = new()
        {
            Audio       = Audio.Clone(),
            Video       = Video.Clone(),
            Subtitles   = Subtitles.Clone(),
            Demuxer     = Demuxer.Clone(),
            Decoder     = Decoder.Clone(),
            Player      = Player.Clone()
        };

        config.Player.config = config;
        config.Demuxer.config = config;

        return config;
    }
    public static Config Load(string path, JsonSerializerOptions jsonOptions = null)
    {
        Config config       = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), jsonOptions);
        config.Loaded       = true;
        config.LoadedPath   = path;

        if (config.Audio.FiltersEnabled && Engine.Config.FFmpegLoadProfile == LoadProfile.Main)
            config.Audio.FiltersEnabled = false;

        // TODO: L: refactor
        config.Player.config = config;
        config.Demuxer.config = config;

        config.Subtitles.SetChildren();

        // Restore the plugin options initialized by the constructor, as they are overwritten after deserialization.

        // Remove removed plugin options
        foreach (var plugin in config.Plugins)
        {
            // plugin deleted
            if (!config.defaultPlugins.ContainsKey(plugin.Key))
            {
                config.Plugins.Remove(plugin.Key);
                continue;
            }

            // plugin option deleted
            foreach (var opt in plugin.Value)
                if (!config.defaultPlugins[plugin.Key].ContainsKey(opt.Key))
                    config.Plugins[plugin.Key].Remove(opt.Key);
        }

        // Restore added plugin options
        foreach (var plugin in config.defaultPlugins)
        {
            // plugin added
            if (!config.Plugins.ContainsKey(plugin.Key))
            {
                config.Plugins[plugin.Key] = plugin.Value;
                continue;
            }

            // plugin option added
            foreach (var opt in plugin.Value)
                if (!config.Plugins[plugin.Key].ContainsKey(opt.Key))
                    config.Plugins[plugin.Key][opt.Key] = opt.Value;
        }

        config.UpdateDefault();

        return config;
    }
    public void Save(string path = null, JsonSerializerOptions jsonOptions = null)
    {
        if (path == null)
        {
            if (string.IsNullOrEmpty(LoadedPath))
                return;

            path = LoadedPath;
        }

        jsonOptions ??= jsonOpts;

        // TBR: Just sync filter values? *We need to know the current VP or keep the last used VP
        if (Video.player != null && Video.player.Renderer != null)
            Video.player.Renderer.SyncFilters();

        File.WriteAllText(path, JsonSerializer.Serialize(this, jsonOptions));
    }

    private void UpdateDefault()
    {
        bool parsed = System.Version.TryParse(Version, out var loadedVer);

        if (!parsed || loadedVer <= System.Version.Parse("0.2.1"))
        {
            // for FlyleafLib v3.8.3, Ensure extension_picky is set
            Demuxer.FormatOpt = DemuxerConfig.DefaultVideoFormatOpt();
            Demuxer.AudioFormatOpt = DemuxerConfig.DefaultVideoFormatOpt();
            Demuxer.SubtitlesFormatOpt = DemuxerConfig.DefaultVideoFormatOpt();


            // for subtitles search #91
            int ctrlFBindingIdx = Player.KeyBindings.Keys
                .FindIndex(k => k.Key == System.Windows.Input.Key.F &&
                             k.Ctrl && !k.Alt && !k.Shift);
            if (ctrlFBindingIdx != -1)
            {
                // remove existing binding
                Player.KeyBindings.Keys.RemoveAt(ctrlFBindingIdx);
            }
            // set CTRL+F to subtitles search
            Player.KeyBindings.Keys.Add(new KeyBinding { Ctrl = true, Key = System.Windows.Input.Key.F, IsKeyUp = true, Action = KeyBindingAction.Custom, ActionName = "ActivateSubsSearch" });

            // Toggle always on top
            int ctrlTBindingIdx = Player.KeyBindings.Keys
                .FindIndex(k => k.Key == System.Windows.Input.Key.T &&
                             k.Ctrl && !k.Alt && !k.Shift);
            if (ctrlTBindingIdx == -1)
            {
                Player.KeyBindings.Keys.Add(new KeyBinding { Ctrl = true, Key = System.Windows.Input.Key.T, IsKeyUp = true, Action = KeyBindingAction.Custom, ActionName = "ToggleAlwaysOnTop" });
            }

            // Set Ctrl+A for ToggleSubsAutoTextCopy (previous Alt+A)
            int ctrlABindingIdx = Player.KeyBindings.Keys
                .FindIndex(k => k.Key == System.Windows.Input.Key.A &&
                             k.Ctrl && !k.Alt && !k.Shift);
            if (ctrlABindingIdx == -1)
            {
                Player.KeyBindings.Keys.Add(new KeyBinding { Ctrl = true, Key = System.Windows.Input.Key.A, IsKeyUp = true, Action = KeyBindingAction.Custom, ActionName = "ToggleSubsAutoTextCopy" });
            }
        }
    }

    internal void SetPlayer(Player player)
    {
        Player.player   = player;
        Player.KeyBindings.SetPlayer(player);
        Demuxer.player  = player;
        Decoder.player  = player;
        Audio.player    = player;
        Video.player    = player;
        Subtitles.SetPlayer(player);
    }

    public string           Version     { get; set; }

    /// <summary>
    /// Whether configuration has been loaded from file
    /// </summary>
    [JsonIgnore]
    public bool             Loaded      { get; private set; }

    /// <summary>
    /// The path that this configuration has been loaded from
    /// </summary>
    [JsonIgnore]
    public string           LoadedPath  { get; private set; }

    public PlayerConfig     Player      { get; set; } = new();
    public DemuxerConfig    Demuxer     { get; set; } = new();
    public DecoderConfig    Decoder     { get; set; } = new();
    public VideoConfig      Video       { get; set; } = new();
    public AudioConfig      Audio       { get; set; } = new();
    public SubtitlesConfig  Subtitles   { get; set; } = new();
    public DataConfig       Data        { get; set; } = new();

    public Dictionary<string, ObservableDictionary<string, string>>
                            Plugins     { get; set; } = [];
    private
           Dictionary<string, ObservableDictionary<string, string>>
                            defaultPlugins;

    public class PlayerConfig : NotifyPropertyChanged
    {
        public PlayerConfig Clone()
        {
            PlayerConfig player = (PlayerConfig) MemberwiseClone();
            player.player = null;
            player.config = null;
            player.KeyBindings = KeyBindings.Clone();
            return player;
        }

        internal Player player;
        internal Config config;

        /// <summary>
        /// It will automatically start playing after open or seek after ended
        /// </summary>
        public bool     AutoPlay                    { get; set; } = true;

        /// <summary>
        /// Required buffered duration ticks before playing
        /// </summary>
        public long     MinBufferDuration {
            get => _MinBufferDuration;
            set
            {
                if (!Set(ref _MinBufferDuration, value)) return;
                if (config != null && value > config.Demuxer.BufferDuration)
                    config.Demuxer.BufferDuration = value;
            }
        }
        long _MinBufferDuration = 500 * 10000;

        /// <summary>
        /// Key bindings configuration
        /// </summary>
        public KeysConfig
                        KeyBindings                 { get; set; } = new KeysConfig();

        /// <summary>
        /// Zero Latency forces playback at the very last frame received (Live Video Only)
        /// </summary>
        public bool     ZeroLatency                 { get => _ZeroLatency; set { Set(ref _ZeroLatency, value); if (config != null) config.Decoder.LowDelay = value; if (player != null && player.IsPlaying) { player.Pause(); player.Play(); } } }
        bool _ZeroLatency;

        /// <summary>
        /// Max Latency (ticks) forces playback (with speed x1+) to stay at the end of the live network stream (default: 0 - disabled)
        /// </summary>
        public long     MaxLatency {
            get => _MaxLatency;
            set
            {
                if (value < 0)
                    value = 0;

                if (!Set(ref _MaxLatency, value)) return;

                if (value == 0)
                {
                    if (player != null)
                        player.Speed = 1;

                    if (config != null)
                        config.Decoder.LowDelay = false;

                    return;
                }

                // Large max buffer so we ensure the actual latency distance
                if (config != null)
                {
                    if (config.Demuxer.BufferDuration < value * 2)
                        config.Demuxer.BufferDuration = value * 2;

                    config.Decoder.LowDelay = true;
                }

                // Small min buffer to avoid enabling latency speed directly
                if (_MinBufferDuration > value / 10)
                    MinBufferDuration = value / 10;
            }
        }
        long _MaxLatency = 0;

        /// <summary>
        /// Min Latency (ticks) prevents MaxLatency to go (with speed x1) less than this limit (default: 0 - as low as possible)
        /// </summary>
        public long     MinLatency                  { get => _MinLatency; set => Set(ref _MinLatency, value); }
        long _MinLatency = 0;

        /// <summary>
        /// Prevents frequent speed changes when MaxLatency is enabled (to avoid audio/video gaps and desyncs)
        /// </summary>
        public long     LatencySpeedChangeInterval  { get; set; } = TimeSpan.FromMilliseconds(700).Ticks;

        /// <summary>
        /// Folder to save recordings (when filename is not specified)
        /// </summary>
        public string   FolderRecordings            { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");

        /// <summary>
        /// Folder to save snapshots (when filename is not specified)
        /// </summary>

        public string   FolderSnapshots             { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Snapshots");

        /// <summary>
        /// Forces CurTime/SeekBackward/SeekForward to seek accurate on video
        /// </summary>
        public bool     SeekAccurate                { get => _SeekAccurate; set => Set(ref _SeekAccurate, value); }
        bool _SeekAccurate;

        /// <summary>
        /// Margin time to move back forward when doing an exact seek (ticks)
        /// </summary>
        public long     SeekAccurateFixMargin       { get; set => Set(ref field, value); } = TimeSpan.FromMilliseconds(0).Ticks;

        /// <summary>
        /// Snapshot encoding will be used (valid formats bmp, png, jpg/jpeg)
        /// </summary>
        public string   SnapshotFormat              { get ;set; } = "bmp";

        /// <summary>
        /// Whether to refresh statistics about bitrates/fps/drops etc.
        /// </summary>
        public bool     Stats                       { get => _Stats; set => Set(ref _Stats, value); }
        bool _Stats = false;

        /// <summary>
        /// Sets playback's thread priority
        /// </summary>
        public ThreadPriority
                        ThreadPriority              { get; set; } = ThreadPriority.AboveNormal;

        /// <summary>
        /// Refreshes CurTime in UI on every frame (can cause performance issues)
        /// </summary>
        public UIRefreshType
                        UICurTime                   { get; set; } = UIRefreshType.PerFrameSecond;

        /// <summary>
        /// The purpose of the player
        /// </summary>
        public Usage    Usage                       { get; set; } = Usage.AVS;

        public long     SeekOffset                  { get; set; } = 1 * (long)1000 * 10000;
        public long     SeekOffset2                 { get; set; } = 5 * (long)1000 * 10000;
        public long     SeekOffset3                 { get; set; } = 15 * (long)1000 * 10000;
        public long     SeekOffset4                 { get; set; } = 30 * (long)1000 * 10000;
        public bool     SeekOffsetAccurate          { get; set; } = true;
        public bool     SeekOffsetAccurate2         { get; set; } = false;
        public bool     SeekOffsetAccurate3         { get; set; } = false;
        public bool     SeekOffsetAccurate4         { get; set; } = false;
        public double   SpeedOffset                 { get; set; } = 0.10;
        public double   SpeedOffset2                { get; set; } = 0.25;
    }
    public class DemuxerConfig : NotifyPropertyChanged
    {
        public DemuxerConfig Clone()
        {
            DemuxerConfig demuxer = (DemuxerConfig) MemberwiseClone();

            demuxer.FormatOpt           = [];
            demuxer.AudioFormatOpt      = [];
            demuxer.SubtitlesFormatOpt  = [];

            foreach (var kv in FormatOpt) demuxer.FormatOpt.Add(kv.Key, kv.Value);
            foreach (var kv in AudioFormatOpt) demuxer.AudioFormatOpt.Add(kv.Key, kv.Value);
            foreach (var kv in SubtitlesFormatOpt) demuxer.SubtitlesFormatOpt.Add(kv.Key, kv.Value);

            demuxer.player = null;
            demuxer.config = null;

            return demuxer;
        }

        internal Player player;
        internal Config config;

        /// <summary>
        /// Whethere to allow avformat_find_stream_info during open (avoiding this can open the input faster but it could cause other issues)
        /// </summary>
        public bool             AllowFindStreamInfo { get; set; } = true;

        /// <summary>
        /// Whether to enable demuxer's custom interrupt callback (for timeouts and interrupts)
        /// </summary>
        public bool             AllowInterrupts     { get; set; } = true;

        /// <summary>
        /// Whether to allow interrupts during av_read_frame
        /// </summary>
        public bool             AllowReadInterrupts { get; set; } = true;

        /// <summary>
        /// Whether to allow timeouts checks within the interrupts callback
        /// </summary>
        public bool             AllowTimeouts       { get; set; } = true;

        /// <summary>
        /// List of FFmpeg formats to be excluded from interrupts
        /// </summary>
        public List<string>     ExcludeInterruptFmts{ get; set; } = ["rtsp"];

        /// <summary>
        /// Maximum allowed duration ticks for buffering
        /// </summary>
        public long             BufferDuration      {
            get => _BufferDuration;
            set
            {
                if (!Set(ref _BufferDuration, value)) return;
                if (config != null && value < config.Player.MinBufferDuration)
                   config.Player.MinBufferDuration = value;
            }
        }
        long _BufferDuration = 30 * (long)1000 * 10000;

        /// <summary>
        /// Maximuim allowed packets for buffering (as an extra check along with BufferDuration)
        /// </summary>
        public long             BufferPackets       { get; set; }

        /// <summary>
        /// Maximuim allowed audio packets (when reached it will drop the extra packets and will fire the AudioLimit event)
        /// </summary>
        public long             MaxAudioPackets     { get; set; }

        /// <summary>
        /// Maximum allowed errors before stopping
        /// </summary>
        public int              MaxErrors           { get; set; } = 30;

        /// <summary>
        /// Custom IO Stream buffer size (in bytes) for the AVIO Context
        /// </summary>
        public int              IOStreamBufferSize  { get; set; } = 0x200000;

        /// <summary>
        /// avformat_close_input timeout (ticks) for protocols that support interrupts
        /// </summary>
        public long             CloseTimeout        { get => closeTimeout; set { closeTimeout = value; closeTimeoutMs = value / 10000; } }
        private long closeTimeout = 1 * 1000 * 10000;
        internal long closeTimeoutMs = 1 * 1000;

        /// <summary>
        /// avformat_open_input + avformat_find_stream_info timeout (ticks) for protocols that support interrupts (should be related to probesize/analyzeduration)
        /// </summary>
        public long             OpenTimeout         { get => openTimeout; set { openTimeout = value; openTimeoutMs = value / 10000; } }
        private long openTimeout = 5 * 60 * (long)1000 * 10000;
        internal long openTimeoutMs = 5 * 60 * 1000;

        /// <summary>
        /// av_read_frame timeout (ticks) for protocols that support interrupts
        /// </summary>
        public long             ReadTimeout         { get => readTimeout; set { readTimeout = value; readTimeoutMs = value / 10000; } }
        private long readTimeout = 10 * 1000 * 10000;
        internal long readTimeoutMs = 10 * 1000;

        /// <summary>
        /// av_read_frame timeout (ticks) for protocols that support interrupts (for Live streams)
        /// </summary>
        public long             ReadLiveTimeout     { get => readLiveTimeout; set { readLiveTimeout = value; readLiveTimeoutMs = value / 10000; } }
        private long readLiveTimeout = 20 * 1000 * 10000;
        internal long readLiveTimeoutMs = 20 * 1000;

        /// <summary>
        /// av_seek_frame timeout (ticks) for protocols that support interrupts
        /// </summary>
        public long             SeekTimeout         { get => seekTimeout; set { seekTimeout = value; seekTimeoutMs = value / 10000; } }
        private long seekTimeout = 8 * 1000 * 10000;
        internal long seekTimeoutMs = 8 * 1000;

        /// <summary>
        /// Forces Input Format
        /// </summary>
        public string           ForceFormat         { get; set; }

        /// <summary>
        /// Forces FPS for NoTimestamp formats (such as h264/hevc)
        /// </summary>
        public double           ForceFPS            { get; set; }

        /// <summary>
        /// FFmpeg's format flags for demuxer (see https://ffmpeg.org/doxygen/trunk/avformat_8h.html)
        /// eg. FormatFlags |= 0x40; // For AVFMT_FLAG_NOBUFFER
        /// </summary>
        public DemuxerFlags     FormatFlags         { get; set; } = DemuxerFlags.DiscardCorrupt;// FFmpeg.AutoGen.ffmpeg.AVFMT_FLAG_DISCARD_CORRUPT;

        /// <summary>
        /// Certain muxers and demuxers do nesting (they open one or more additional internal format contexts). This will pass the FormatOpt and HTTPQuery params to the underlying contexts)
        /// </summary>
        public bool             FormatOptToUnderlying
                                                    { get; set; }

        /// <summary>
        /// Passes original's Url HTTP Query String parameters to underlying
        /// </summary>
        public bool             DefaultHTTPQueryToUnderlying
                                                    { get; set; } = true;

        /// <summary>
        /// HTTP Query String parameters to pass to underlying
        /// </summary>
        public Dictionary<string, string>
                                ExtraHTTPQueryParamsToUnderlying
                                                    { get; set; } = [];

        /// <summary>
        /// FFmpeg's format options for demuxer
        /// </summary>
        public Dictionary<string, string>
                                FormatOpt           { get; set; } = DefaultVideoFormatOpt();
        public Dictionary<string, string>
                                AudioFormatOpt      { get; set; } = DefaultVideoFormatOpt();

        public Dictionary<string, string>
                                SubtitlesFormatOpt  { get; set; } = DefaultVideoFormatOpt();

        public static Dictionary<string, string> DefaultVideoFormatOpt()
        {
            // TODO: Those should be set based on the demuxer format/protocol (to avoid false warnings about invalid options and best fit for the input, eg. live stream)

            Dictionary<string, string> defaults = new()
            {
                // General
                { "probesize",          (50 * (long)1024 * 1024).ToString() },      // (Bytes) Default 5MB | Higher for weird formats (such as .ts?) and 4K/Hevc
                { "analyzeduration",    (10 * (long)1000 * 1000).ToString() },      // (Microseconds) Default 5 seconds | Higher for network streams

                // HTTP
                { "reconnect",          "1" },                                       // auto reconnect after disconnect before EOF
                { "reconnect_streamed", "1" },                                       // auto reconnect streamed / non seekable streams (this can cause issues with HLS ts segments - disable this or http_persistent)
                { "reconnect_delay_max","7" },                                       // max reconnect delay in seconds after which to give up
                { "user_agent",         "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.159 Safari/537.36" },

                { "extension_picky",    "0" },                                       // Added in ffmpeg v7.1.1 and causes issues when enabled with allowed extentions #577

                // HLS
                { "http_persistent",    "0" },                                       // Disables keep alive for HLS - mainly when use reconnect_streamed and non-live HLS streams

                // RTSP
                { "rtsp_transport",     "tcp" },                                     // Seems UDP causing issues
            };

            //defaults.Add("live_start_index",   "-1");
            //defaults.Add("timeout",           (2 * (long)1000 * 1000).ToString());      // (Bytes) Default 5MB | Higher for weird formats (such as .ts?)
            //defaults.Add("rw_timeout",     (2 * (long)1000 * 1000).ToString());      // (Microseconds) Default 5 seconds | Higher for network streams

            return defaults;
        }

        public Dictionary<string, string> GetFormatOptPtr(MediaType type)
            => type == MediaType.Video ? FormatOpt : type == MediaType.Audio ? AudioFormatOpt : SubtitlesFormatOpt;
    }
    public class DecoderConfig : NotifyPropertyChanged
    {
        internal Player player;

        public DecoderConfig Clone()
        {
            DecoderConfig decoder = (DecoderConfig) MemberwiseClone();
            decoder.player = null;

            return decoder;
        }

        /// <summary>
        /// Threads that will be used from the decoder
        /// </summary>
        public int              VideoThreads        { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Maximum video frames to be decoded and processed for rendering
        /// </summary>
        public int              MaxVideoFrames      { get => _MaxVideoFrames;   set { if (Set(ref _MaxVideoFrames, value)) { player?.RefreshMaxVideoFrames(); } } }
        int _MaxVideoFrames = 4;
        internal void SetMaxVideoFrames(int maxVideoFrames) { _MaxVideoFrames = maxVideoFrames; RaiseUI(nameof(MaxVideoFrames)); } // can be updated by video decoder if fails to allocate them

        /// <summary>
        /// Maximum video frames to be decoded and processed for rendering
        /// </summary>
        public int              MaxVideoFramesPrev  { get => _MaxVideoFramesPrev;   set { if (Set(ref _MaxVideoFramesPrev, value)) { player?.RefreshMaxVideoFrames(); } } }
        int _MaxVideoFramesPrev = 0;

        /// <summary>
        /// Maximum audio frames to be decoded and processed for playback
        /// </summary>
        public int              MaxAudioFrames      { get; set; } = 5;

        /// <summary>
        /// Maximum subtitle frames to be decoded
        /// </summary>
        public int              MaxSubsFrames       { get; set; } = 1;

        /// <summary>
        /// Maximum data frames to be decoded
        /// </summary>
        public int              MaxDataFrames       { get; set; } = 100;

        /// <summary>
        /// Maximum allowed errors before stopping
        /// </summary>
        public int              MaxErrors           { get; set; } = 200;

        /// <summary>
        /// Allows video accceleration even in codec's profile mismatch
        /// </summary>
        public bool             AllowProfileMismatch{ get => _AllowProfileMismatch; set => Set(ref _AllowProfileMismatch, value); }
        bool _AllowProfileMismatch = false;

        /// <summary>
        /// Allows corrupted frames (Parses AV_CODEC_FLAG_OUTPUT_CORRUPT to AVCodecContext)
        /// </summary>
        public bool             ShowCorrupted       { get => _ShowCorrupted;        set => Set(ref _ShowCorrupted, value); }
        bool _ShowCorrupted;

        /// <summary>
        /// Forces low delay (Parses AV_CODEC_FLAG2_FAST or AV_CODEC_FLAG_LOW_DELAY -based on DropFrames- to AVCodecContext) (auto-enabled with MaxLatency)
        /// </summary>
        public bool             LowDelay            { get => _LowDelay;             set => Set(ref _LowDelay, value); }
        bool _LowDelay;

        /// <summary>
        /// Sets AVCodecContext skip_frame to None or Default (affects also LowDelay)
        /// </summary>
        public bool             AllowDropFrames     { get => _AllowDropFrames;      set => Set(ref _AllowDropFrames, value); }
        bool _AllowDropFrames;

        public string           AudioCodec          { get => _AudioCodec;           set => Set(ref _AudioCodec, value); }
        internal string _AudioCodec;

        public string           VideoCodec          { get => _VideoCodec;           set => Set(ref _VideoCodec, value); }
        internal string _VideoCodec;

        public string           SubtitlesCodec      { get => _SubtitlesCodec;       set => Set(ref _SubtitlesCodec, value); }
        internal string _SubtitlesCodec;

        public string GetCodecPtr(MediaType type)
            => type == MediaType.Video ? _VideoCodec : type == MediaType.Audio ? _AudioCodec : _SubtitlesCodec;

        public Dictionary<string, string>
                                AudioCodecOpt       { get; set; } = [];
        public Dictionary<string, string>
                                VideoCodecOpt       { get; set; } = [];
        public Dictionary<string, string>
                                SubtitlesCodecOpt   { get; set; } = [];

        public Dictionary<string, string> GetCodecOptPtr(MediaType type)
            => type == MediaType.Video ? VideoCodecOpt : type == MediaType.Audio ? AudioCodecOpt : SubtitlesCodecOpt;
    }
    public class VideoConfig : VPConfig
    {
        public VideoConfig()
        {
            UIInvokeIfRequired(() =>
            {
                BindingOperations.EnableCollectionSynchronization(FLFilters, lockFLFilters);
                BindingOperations.EnableCollectionSynchronization(D3Filters, lockD3Filters);
            });
        }

        public VideoConfig Clone()
        {
            VideoConfig video = (VideoConfig) MemberwiseClone();
            video.player = null;

            return video;
        }

        internal Player player { get => _player; set { _player = value; vp = value != null ? value.Renderer : null; } }
        Player _player;

        /// <summary>
        /// Whether video should be allowed
        /// </summary>
        public bool             Enabled                     { get => _Enabled;          set { if (Set(ref _Enabled, value)) if (value) player?.Video.Enable(); else player?.Video.Disable(); } }
        bool _Enabled = true;
        internal void SetEnabled(bool enabled)              => Set(ref _Enabled, enabled, true, nameof(Enabled));
        public void Toggle() => Enabled = !Enabled;

        /// <summary>
        /// <para>Forces a specific GPU Adapter to be used by the renderer</para>
        /// <para>GPUAdapter must match with the description of the adapter eg. rx 580 (available adapters can be found in Engine.Video.GPUAdapters)</para>
        /// </summary>
        public string           GPUAdapter                  { get; set; }

        /// <summary>
        /// Embedded alpha channel in HW decoded -horizontal/vertical- split frame (FlyleafVP only)
        /// </summary>
        public SplitFrameAlphaPosition
                                SplitFrameAlphaPosition     { get => _SplitFrameAlphaPosition;   set => Set(ref _SplitFrameAlphaPosition, value); }
        internal SplitFrameAlphaPosition _SplitFrameAlphaPosition;

        /// <summary>
        /// Clears the screen on stop/close/open
        /// </summary>
        public bool             ClearScreen                 { get; set; } = true;

        /// <summary>
        /// Used to limit the number of frames rendered, particularly at increased speed
        /// </summary>
        public double           MaxOutputFps                { get; set; } = 60;

        /// <summary>
        /// The max resolution that the current system can achieve and will be used from the input/stream suggester plugins
        /// </summary>
        [JsonIgnore]
        public int              MaxVerticalResolutionAuto   { get; internal set; }

        /// <summary>
        /// Custom max vertical resolution that will be used from the input/stream suggester plugins
        /// </summary>
        public int              MaxVerticalResolutionCustom { get => _MaxVerticalResolutionCustom;  set => Set(ref _MaxVerticalResolutionCustom, value); }
        int _MaxVerticalResolutionCustom;

        /// <summary>
        /// The max resolution that is currently used (based on Auto/Custom)
        /// </summary>
        [JsonIgnore]
        public int              MaxVerticalResolution       => MaxVerticalResolutionCustom == 0 ? (MaxVerticalResolutionAuto != 0 ? MaxVerticalResolutionAuto : 1080) : MaxVerticalResolutionCustom;

        /// <summary>
        /// Sets Super Resolution (Nvidia / Intel - D3D11VP only)
        /// </summary>
        public bool             SuperResolution             { get => _SuperResolution;  set { if (Set(ref _SuperResolution, value)) player?.Renderer?.VPRequest(VPRequestType.Viewport); } }
        internal bool _SuperResolution;

        /// <summary>
        /// Forces SwsScale instead of FlyleafVP for non HW decoded frames
        /// </summary>
        public bool             SwsForce                    { get; set; } = false;

        /// <summary>
        /// Activates Direct3D video acceleration (decoding)
        /// </summary>
        public bool             VideoAcceleration           { get; set => Set(ref field, value); } = true;
        public void ToggleVideoAcceleration() => VideoAcceleration = !VideoAcceleration;

        /// <summary>
        /// Whether to use embedded video processor with custom pixel shaders or D3D11<br/>
        /// * FLVP supports HDR to SDR, hardware/software frames and more formats<br/>
        /// * D3D11 uses less power, supports only hardware frames, deinterlace, super resolution and more accurate filters based on gpu
        /// </summary>
        public VideoProcessors  VideoProcessor              { get => _VideoProcessor;   set { if (Set(ref _VideoProcessor, value))  player?.Renderer?.VPSwitchCheck(); } }
        VideoProcessors _VideoProcessor = VideoProcessors.Auto;

        /// <summary>
        /// Sets DeInterlace (D3D11VP only)
        /// </summary>
        public DeInterlace      DeInterlace                 { get => _DeInterlace;      set { if (Set(ref _DeInterlace, value))     player?.Renderer?.VPRequest(VPRequestType.Deinterlace); } }
        DeInterlace _DeInterlace = DeInterlace.Auto;

        /// <summary>
        /// Whether to use double rate (Bob) for DeInterlace instead normal (Weave/Blend)
        /// </summary>
        public bool             DoubleRate                  { get => _DoubleRate;       set => Set(ref _DoubleRate, value); }
        bool _DoubleRate = true;

        /// <summary>
        /// The HDR to SDR method that will be used by the pixel shader
        /// </summary>
        public HDRtoSDRMethod   HDRtoSDRMethod              { get => _HDRtoSDRMethod;   set { if (Set(ref _HDRtoSDRMethod, value))  player?.Renderer?.VPRequest(VPRequestType.HDRtoSDR); } }
        HDRtoSDRMethod _HDRtoSDRMethod = HDRtoSDRMethod.Hable;

       /// <summary>
        /// SDR Display Peak Luminance - tonemap for HDR to SDR (based on Auto/Custom)
        /// </summary>
        [JsonIgnore]
        public float SDRDisplayNits
        {
            get => SDRDisplayNitsCustom == 0 ? (SDRDisplayNitsAuto != 0 ? SDRDisplayNitsAuto : 200) : SDRDisplayNitsCustom;
            set => SDRDisplayNitsCustom = value;
        }

        /// <summary>
        /// SDR Display Peak Luminance - tonemap for HDR to SDR (Recommended)
        /// </summary>
        [JsonIgnore]
        public float            SDRDisplayNitsAuto          { get => _SDRDisplayNitsAuto;   set { if (Set(ref _SDRDisplayNitsAuto, value) && _SDRDisplayNitsCustom == 0) { player?.Renderer?.VPRequest(VPRequestType.HDRtoSDR); RaiseUI(nameof(SDRDisplayNits)); } } }
        float _SDRDisplayNitsAuto;

        /// <summary>
        /// SDR Display Peak Luminance - tonemap for HDR to SDR (Custom)
        /// </summary>
        public float            SDRDisplayNitsCustom        { get => _SDRDisplayNitsCustom; set { if (Set(ref _SDRDisplayNitsCustom, value)) { player?.Renderer?.VPRequest(VPRequestType.HDRtoSDR); } } }
        float _SDRDisplayNitsCustom;

        //public SwapChainFormat  SwapChainFormat             { get; set; } = SwapChainFormat.BGRA;

        /// <summary>
        /// Enables custom Direct2D drawing over playback frames
        /// </summary>
        public bool             Use2DGraphics               { get; set; }

        /// <summary>
        /// Scaling quality used for bitmap subtitle rendering.
        /// </summary>
        public SwsFlags         BitmapSubsScaleQuality      { get; set; } = SwsFlags.Bilinear | SwsFlags.Bitexact;

        public event EventHandler<ID2D1DeviceContext> D2DInitialized;
        public event EventHandler<ID2D1DeviceContext> D2DDisposing;
        public event EventHandler<ID2D1DeviceContext> D2DDraw;

        internal void OnD2DInitialized(Renderer renderer, ID2D1DeviceContext context)
            => D2DInitialized?.Invoke(renderer, context);

        internal void OnD2DDisposing(Renderer renderer, ID2D1DeviceContext context)
            => D2DDisposing?.Invoke(renderer, context);

        internal void OnD2DDraw(Renderer renderer, ID2D1DeviceContext context)
            => D2DDraw?.Invoke(renderer, context);

        /// <summary>
        /// When you change a filter value from one VP will update also the other if exists (however might not exact same picture output)
        /// </summary>
        public bool             SyncVPFilters               { get; set; } = true;
        public ObservableDictionary<FLFilters, FLFilter>
                                FLFilters                   { get ; set; } = [];
        public ObservableDictionary<VideoProcessorFilter, D3Filter>
                                D3Filters                   { get ; set; } = [];

        internal bool flFiltersFilled;
        internal bool d3FiltersFilled;
        internal bool hasFLFilters;
        internal bool hasD3Filters;
        internal readonly object lockFLFilters  = new();
        internal readonly object lockD3Filters  = new();
    }
    public class AudioConfig : NotifyPropertyChanged
    {
        public AudioConfig Clone()
        {
            AudioConfig audio = (AudioConfig) MemberwiseClone();
            audio.player = null;

            return audio;
        }

        internal Player player;

        /// <summary>
        /// Whether audio should allowed
        /// </summary>
        public bool             Enabled             { get => _Enabled;          set { if (Set(ref _Enabled, value)) if (value) player?.Audio.Enable(); else player?.Audio.Disable(); } }
        bool _Enabled = true;
        internal void SetEnabled(bool enabled)      => Set(ref _Enabled, enabled, true, nameof(Enabled));
        public void Toggle() => Enabled = !Enabled;

        /// <summary>
        /// Audio delay ticks (will be reseted to 0 for every new audio stream)
        /// </summary>
        [JsonIgnore] // We reset this on open (TBR: Resync should be Task?* can block UI | Same for Enable)
        public long             Delay               { get => _Delay;            set { if (player != null && !player.Audio.IsOpened) return;  if (Set(ref _Delay, value)) player?.ReSync(player.decoder.AudioStream); } }
        long _Delay;
        internal void SetDelay(long delay)          => Set(ref _Delay, delay, true, nameof(Delay));

        public long             DelayOffset         { get; set; } =  100 * 10000;
        public long             DelayOffset2        { get; set; } = 1000 * 10000;

        public void DelayAdd()      => Delay += DelayOffset;
        public void DelayRemove()   => Delay -= DelayOffset;
        public void DelayAdd2()     => Delay += DelayOffset2;
        public void DelayRemove2()  => Delay -= DelayOffset2;

        public int              VolumeOffset        { get; set; } = 5;

        /// <summary>
        /// The upper limit of the volume amplifier
        /// </summary>
        public int              VolumeMax           { get => _VolumeMax; set { Set(ref _VolumeMax, value); if (player != null && player.Audio.masteringVoice != null) player.Audio.masteringVoice.Volume = value / 100f;  } }
        int _VolumeMax = 150;

        /// <summary>
        /// The default volume
        /// </summary>
        public int              VolumeDefault       { get; set => Set(ref field, value); } = 75;

        public void ToggleMute()    => player.Audio.Mute = !player.Audio.Mute;
        public void VolumeUp()
        {
            if (player.Audio.Volume == VolumeMax) return;
            player.Audio.Volume = Math.Min(player.Audio.Volume + VolumeOffset, VolumeMax);
        }
        public void VolumeDown()
        {
            if (player.Audio.Volume == 0) return;
            player.Audio.Volume = Math.Max(player.Audio.Volume - VolumeOffset, 0);
        }

        /// <summary>
        /// Uses FFmpeg filters instead of Swr (better speed quality and support for extra filters, requires avfilter-X.dll)
        /// </summary>
        public bool             FiltersEnabled      { get => _FiltersEnabled; set { if (Set(ref _FiltersEnabled, value && Engine.Config.FFmpegLoadProfile != LoadProfile.Main)) player?.AudioDecoder.SetupFiltersOrSwr(); } }
        bool _FiltersEnabled = true;

        /// <summary>
        /// List of filters for post processing the audio samples (experimental)<br/>
        /// (Requires FiltersEnabled)
        /// </summary>
        public List<Filter>     Filters             { get; set; }

        /// <summary>
        /// Reloads filters from Config.Audio.Filters (experimental)
        /// </summary>
        /// <returns>0 on success</returns>
        public int ReloadFilters() => player.AudioDecoder.ReloadFilters();

        /// <summary>
        /// <para>
        /// Updates filter's property (experimental)
        /// Note: This will not update the property value in Config.Audio.Filters
        /// </para>
        /// </summary>
        /// <param name="filterId">Filter's unique id specified in Config.Audio.Filters</param>
        /// <param name="key">Filter's property to change</param>
        /// <param name="value">Filter's property value</param>
        /// <returns>0 on success</returns>
        public int UpdateFilter(string filterId, string key, string value) => player.AudioDecoder.UpdateFilter(filterId, key, value);

        /// <summary>
        /// Audio languages preference by priority
        /// </summary>
        public List<Language>   Languages
        {
            get
            {
                field ??= GetSystemLanguages();
                return field;
            }
            set => Set(ref field, value);
        }

    }

    public class SubConfig : NotifyPropertyChanged
    {
        internal Player player;

        public SubConfig()
        {
        }

        public SubConfig(int subIndex)
        {
            SubIndex = subIndex;
        }

        [JsonIgnore]
        public int SubIndex { get; set => Set(ref field, value); }

        [JsonIgnore]
        public bool EnabledTranslated
        {
            get;
            set
            {
                if (Set(ref field, value))
                {
                    if (player == null)
                    {
                        return;
                    }

                    // Clear once to update the subtitle being displayed.
                    player.sFramesPrev[SubIndex] = null;
                    player.SubtitleClear(SubIndex);

                    // Switching the display while leaving the translated text itself
                    foreach (SubtitleData sub in player.SubtitlesManager[SubIndex].Subs)
                    {
                        sub.EnabledTranslated = field;
                    }

                    // Update text in sidebar
                    player.SubtitlesManager[SubIndex].Refresh();
                }
            }
        } = false;

        /// <summary>
        /// Subtitle delay ticks (will be reset to 0 for every new subtitle stream)
        /// </summary>
        [JsonIgnore] // We reset this on open (TBR: Resync should be Task?* can block UI | Same for Enable)
        public long Delay
        {
            get => _delay;
            set
            {
                if (player == null || !player.Subtitles[SubIndex].Enabled)
                {
                    return;
                }
                if (Set(ref _delay, value))
                {
                    player.SubtitlesManager[SubIndex].SetCurrentTime(new TimeSpan(player.CurTime));
                    player.ReSync(player.decoder.SubtitlesStreams[SubIndex]);
                }
            }
        }
        private long _delay;

        internal void SetDelay(long delay) => Set(ref _delay, delay, true, nameof(Delay));

        public void DelayAdd() => Delay += player != null ? player.Config.Subtitles.DelayOffset : 0;
        public void DelayRemove() => Delay -= player != null ? player.Config.Subtitles.DelayOffset : 0;
        public void DelayAdd2() => Delay += player != null ? player.Config.Subtitles.DelayOffset2 : 0;
        public void DelayRemove2() => Delay -= player != null ? player.Config.Subtitles.DelayOffset2 : 0;

        /// <summary>
        /// Whether subtitle should be visible
        /// TODO: L: should move to AppConfig?
        /// </summary>
        [JsonIgnore]
        public bool Visible { get; set => Set(ref field, value); } = true;
        public void ToggleVisible()
        {
            Visible = !Visible;
        }

        /// <summary>
        /// OCR Engine Type
        /// </summary>
        public SubOCREngineType OCREngine { get; set => Set(ref field, value); } = SubOCREngineType.Tesseract;
    }

    public class SubtitlesConfig : NotifyPropertyChanged
    {
        public SubConfig[] SubConfigs { get; set; }

        public SubConfig this[int subIndex] => SubConfigs[subIndex];

        public SubtitlesConfig()
        {
            int subNum = 2;
            SubConfigs = new SubConfig[subNum];
            for (int i = 0; i < subNum; i++)
            {
                SubConfigs[i] = new SubConfig(i);
            }
        }

        internal void SetChildren()
        {
            for (int i = 0; i < SubConfigs.Length; i++)
            {
                SubConfigs[i].SubIndex = i;
            }
        }

        public SubtitlesConfig Clone()
        {
            SubtitlesConfig subs = (SubtitlesConfig) MemberwiseClone();

            subs.Languages = [];
            if (Languages != null) foreach(var lang in Languages) subs.Languages.Add(lang);

            subs.player = null;

            return subs;
        }

        internal Player player;
        internal void SetPlayer(Player player)
        {
            this.player = player;

            foreach (SubConfig conf in SubConfigs)
            {
                conf.player = player;
            }
        }

        /// <summary>
        /// Whether subtitles should be allowed
        /// </summary>
        public bool             Enabled             { get => _Enabled; set { if(Set(ref _Enabled, value)) if (value) player?.Subtitles.Enable(); else player?.Subtitles.Disable(); } }
        bool _Enabled = true;
        internal void SetEnabled(bool enabled)      => Set(ref _Enabled, enabled, true, nameof(Enabled));
        public void Toggle() => Enabled = !Enabled;

        public void ToggleVisible()
        {
            SubConfigs[0].ToggleVisible();
            SubConfigs[1].Visible = SubConfigs[0].Visible; // align to primary
        }

        public long             DelayOffset    { get; set; } =  100 * 10000;
        public long             DelayOffset2   { get; set; } = 1000 * 10000;

        /// <summary>
        /// Max number of subtitles (currently not configurable)
        /// </summary>
        public int Max { get; set => Set(ref field, value); } = 2;

        /// <summary>
        /// Whether to cache internal bitmap subtitles on memory
        /// Memory usage is larger since all bitmap are read on memory, but has the following advantages
        /// 1. Internal bitmap subtitles can be displayed in the sidebar
        /// 2. Can display bitmap subtitles during playback when seeking (mpv: can, VLC: cannot)
        /// </summary>
        public bool EnabledCached { get; set => Set(ref field, value); } = true;

        public bool OpenAutomaticSubs { get; set => Set(ref field, value); }

        /// <summary>
        /// Subtitle languages preference by priority
        /// </summary>
        public List<Language> Languages
        {
            get
            {
                field ??= GetSystemLanguages();
                return field;
            }
            set => Set(ref field, value);
        }

        /// <summary>
        /// Whether to use automatic language detection
        /// </summary>
        public bool LanguageAutoDetect { get; set => Set(ref field, value); } = true;

        /// <summary>
        /// Language to be used when source language was unknown (primary)
        /// </summary>
        public Language LanguageFallbackPrimary
        {
            get
            {
                field ??= Languages.FirstOrDefault();
                return field;
            }
            set => Set(ref field, value);
        }

        /// <summary>
        /// Language to be used when source language was unknown (secondary)
        /// </summary>
        public Language LanguageFallbackSecondary
        {
            get
            {
                if (LanguageFallbackSecondarySame)
                {
                    return LanguageFallbackPrimary;
                }

                field ??= LanguageFallbackPrimary;

                return field;
            }
            set => Set(ref field, value);
        }

        /// <summary>
        /// Whether to use LanguageFallbackPrimary for secondary subtitles
        /// </summary>
        public bool LanguageFallbackSecondarySame
        {
            get;
            set
            {
                if (Set(ref field, value))
                {
                    Raise(nameof(LanguageFallbackSecondary));
                }
            }
        } = true;

        /// <summary>
        /// Whether to use local search plugins (see also <see cref="SearchLocalOnInputType"/>)
        /// </summary>
        public bool             SearchLocal         { get => _SearchLocal; set => Set(ref _SearchLocal, value); }
        bool _SearchLocal = false;

        public const string DefaultSearchLocalPaths = "subs; subtitles";

        public string           SearchLocalPaths
        {
            get;
            set
            {
                if (Set(ref field, value))
                {
                    CmdResetSearchLocalPaths.OnCanExecuteChanged();
                }
            }
        } = DefaultSearchLocalPaths;

        [JsonIgnore]
        public RelayCommand CmdResetSearchLocalPaths => field ??= new((_) =>
        {
            SearchLocalPaths = DefaultSearchLocalPaths;
        }, (_) => SearchLocalPaths != DefaultSearchLocalPaths);

        /// <summary>
        /// Allowed input types to be searched locally for subtitles (empty list allows all types)
        /// </summary>
        public List<InputType>  SearchLocalOnInputType
                                                    { get; set; } = [InputType.File, InputType.UNC, InputType.Torrent];

        /// <summary>
        /// Whether to use online search plugins (see also <see cref="SearchOnlineOnInputType"/>)
        /// </summary>
        public bool             SearchOnline        { get => _SearchOnline; set { Set(ref _SearchOnline, value); if (player != null && player.Video.isOpened) Task.Run(() => { if (player != null && player.Video.isOpened) player.decoder.SearchOnlineSubtitles(); }); } }
        bool _SearchOnline = false;

        /// <summary>
        /// Allowed input types to be searched online for subtitles (empty list allows all types)
        /// </summary>
        public List<InputType>  SearchOnlineOnInputType
                                                    { get; set; } = [InputType.File, InputType.UNC, InputType.Torrent];

        /// <summary>
        /// Subtitles parser (can be used for custom parsing)
        /// </summary>
        [JsonIgnore]
        public Action<SubtitlesFrame>
                                Parser              { get; set; } = ParseSubtitles.Parse;

        #region ASR
        /// <summary>
        /// ASR Engine Type (Currently only supports OpenAI Whisper)
        /// </summary>
        public SubASREngineType ASREngine { get; set => Set(ref field, value); } = SubASREngineType.WhisperCpp;

        /// <summary>
        /// ASR OpenAI Whisper common config
        /// </summary>
        public WhisperConfig WhisperConfig { get; set => Set(ref field, value); } = new();

        /// <summary>
        /// ASR whisper.cpp config
        /// </summary>
        public WhisperCppConfig WhisperCppConfig { get; set => Set(ref field, value); } = new();

        /// <summary>
        /// ASR Faster-Whisper config
        /// </summary>
        public FasterWhisperConfig FasterWhisperConfig { get; set => Set(ref field, value); } = new();

        /// <summary>
        /// Chunk size (MB) when processing ASR with audio stream
        /// Increasing size will increase memory usage but may result in more natural subtitle breaks
        /// </summary>
        public int ASRChunkSizeMB
        {
            get;
            set
            {
                if (Set(ref field, value))
                {
                    Raise(nameof(ASRChunkSize));
                }
            }
        } = 20;

        [JsonIgnore]
        public long ASRChunkSize => ASRChunkSizeMB * 1024 * 1024;

        /// <summary>
        /// Chunk seconds when processing ASR with audio stream
        /// In the case of network streams, etc., the size is small and can be divided by specifying the number of seconds.
        /// </summary>
        public int ASRChunkSeconds { get; set => Set(ref field, value); } = 20;
        #endregion

        #region OCR
        /// <summary>
        /// OCR Tesseract Region Settings (key: iso6391, value: LangCode)
        /// </summary>
        public Dictionary<string, string> TesseractOcrRegions { get; set => Set(ref field, value); } = new();

        /// <summary>
        /// OCR Microsoft Region Settings (key: iso6391, value: LanguageTag (BCP-47)
        /// </summary>
        public Dictionary<string, string> MsOcrRegions { get; set => Set(ref field, value); } = new();
        #endregion

        #region Translation
        /// <summary>
        /// Language to be translated to
        /// </summary>
        public TargetLanguage TranslateTargetLanguage
        {
            get;
            set
            {
                if (Set(ref field, value))
                {
                    TranslateLanguage = Language.Get(value.ToISO6391());
                }
            }
        } = TargetLanguage.EnglishAmerican;

        [JsonIgnore]
        public Language TranslateLanguage { get; private set; }

        /// <summary>
        /// Translation Service Type
        /// </summary>
        public TranslateServiceType TranslateServiceType { get; set => Set(ref field, value); } = TranslateServiceType.GoogleV1;

        /// <summary>
        /// Translation Word Service Type
        /// </summary>
        public TranslateServiceType TranslateWordServiceType { get; set => Set(ref field, value); } = TranslateServiceType.GoogleV1;

        /// <summary>
        /// Translation Service Type Settings
        /// </summary>
        public Dictionary<TranslateServiceType, ITranslateSettings> TranslateServiceSettings { get; set => Set(ref field, value); } = new();

        /// <summary>
        /// Maximum count backward
        /// </summary>
        public int TranslateCountBackward { get; set => Set(ref field, value); } = 1;

        /// <summary>
        /// Maximum count forward
        /// </summary>
        public int TranslateCountForward { get; set => Set(ref field, value); } = 12;

        /// <summary>
        /// Number of concurrent requests to translation services
        /// </summary>
        public int TranslateMaxConcurrency {
            get;
            set
            {
                if (value <= 0)
                    return;

                Set(ref field, value);
            }
        } = 2;

        /// <summary>
        /// Chat-style LLM API config
        /// </summary>
        public TranslateChatConfig TranslateChatConfig { get; set => Set(ref field, value); } = new();
        #endregion
    }
    public class DataConfig : NotifyPropertyChanged
    {
        public DataConfig Clone()
        {
            DataConfig data = (DataConfig)MemberwiseClone();

            data.player = null;

            return data;
        }

        internal Player player;

        /// <summary>
        /// Whether data should be processed
        /// </summary>
        public bool             Enabled             { get => _Enabled; set { if (Set(ref _Enabled, value)) if (value) player?.Data.Enable(); else player?.Data.Disable(); } }
        bool _Enabled = false;
        internal void SetEnabled(bool enabled) => Set(ref _Enabled, enabled, true, nameof(Enabled));
    }
}

/// <summary>
/// Engine's configuration
/// </summary>
public class EngineConfig
{
    public string   Version                 { get; set; }

    /// <summary>
    /// It will not initiallize audio and will be disabled globally
    /// </summary>
    public bool     DisableAudio            { get; set; }

    /// <summary>
    /// <para>Required to register ffmpeg libraries. Make sure you provide x86 or x64 based on your project.</para>
    /// <para>:&lt;path&gt; for relative path from current folder or any below</para>
    /// <para>&lt;path&gt; for absolute or relative path</para>
    /// </summary>
    public string   FFmpegPath              { get; set; } = "FFmpeg";

    /// <summary>
    /// <para>Can be used to choose which FFmpeg libs to load</para>
    /// All (Devices &amp; Filters)<br/>
    /// Filters<br/>
    /// Main<br/>
    /// </summary>
    public LoadProfile
                    FFmpegLoadProfile       { get; set; } = LoadProfile.Filters; // change default to disable devices

    /// <summary>
    /// Whether to allow HLS live seeking (this can cause segmentation faults in case of incompatible ffmpeg version with library's custom structures)
    /// </summary>
    public bool     FFmpegHLSLiveSeek       { get; set; }

    /// <summary>
    /// Sets FFmpeg logger's level
    /// </summary>
    public Flyleaf.FFmpeg.LogLevel
                    FFmpegLogLevel          { get => _FFmpegLogLevel; set { _FFmpegLogLevel = value; if (Engine.IsLoaded) FFmpegEngine.SetLogLevel(); } }
    Flyleaf.FFmpeg.LogLevel _FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Quiet;

    /// <summary>
    /// Whether configuration has been loaded from file
    /// </summary>
    [JsonIgnore]
    public bool     Loaded                  { get; private set; }

    /// <summary>
    /// The path that this configuration has been loaded from
    /// </summary>
    [JsonIgnore]
    public string   LoadedPath              { get; private set; }

    /// <summary>
    /// <para>Sets loggers output</para>
    /// <para>:debug -> System.Diagnostics.Debug</para>
    /// <para>:console -> System.Console</para>
    /// <para>&lt;path&gt; -> Absolute or relative file path</para>
    /// </summary>
    public string   LogOutput               { get => _LogOutput; set { _LogOutput = value; if (Engine.IsLoaded) SetOutput(); } }
    string _LogOutput = "";

    /// <summary>
    /// Sets logger's level
    /// </summary>
    public LogLevel LogLevel                { get; set; } = LogLevel.Quiet;

    /// <summary>
    /// When the output is file it will append instead of overwriting
    /// </summary>
    public bool     LogAppend               { get; set; }

    /// <summary>
    /// Lines to cache before writing them to file
    /// </summary>
    public int      LogCachedLines          { get; set; } = 20;

    /// <summary>
    /// Max filesize (in bytes) of log before starting a new log file <see cref="LogRollMaxFiles"/>
    /// </summary>
    public long     LogRollMaxFileSize      { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Max number of log files to roll over before deleting the oldest
    /// </summary>
    public long     LogRollMaxFiles         { get; set; } = 0;

    /// <summary>
    /// Sets the logger's datetime string format
    /// </summary>
    public string   LogDateTimeFormat       { get; set; } = "HH.mm.ss.fff";

    /// <summary>
    /// <para>Required to register plugins. Make sure you provide x86 or x64 based on your project and same .NET framework.</para>
    /// <para>:&lt;path&gt; for relative path from current folder or any below</para>
    /// <para>&lt;path&gt; for absolute or relative path</para>
    /// </summary>
    public string   PluginsPath             { get; set; } = "Plugins";

    /// <summary>
    /// <para>Activates Master Thread to monitor all the players and perform the required updates</para>
    /// <para>Required for Activity Mode, Stats &amp; Buffered Duration on Pause</para>
    /// </summary>
    public bool     UIRefresh               { get => _UIRefresh; set { _UIRefresh = value; if (value && Engine.IsLoaded) Engine.StartThread(); } }
    static bool _UIRefresh = true;

    /// <summary>
    /// <para>How often should update the UI in ms (low values can cause performance issues)</para>
    /// <para>Should UIRefreshInterval &lt; 1000ms and 1000 % UIRefreshInterval == 0 for accurate per second stats</para>
    /// </summary>
    public int      UIRefreshInterval       { get; set; } = 250;

    /// <summary>
    /// Loads engine's configuration
    /// </summary>
    /// <param name="path">Absolute or relative path to load the configuraiton</param>
    /// <param name="jsonOptions">JSON serializer options</param>
    // <returns></returns>
    public static EngineConfig Load(string path, JsonSerializerOptions jsonOptions = null)
    {
        EngineConfig config = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(path), jsonOptions);
        config.Loaded       = true;
        config.LoadedPath   = path;

        return config;
    }

    /// <summary>
    /// Saves engine's current configuration
    /// </summary>
    /// <param name="path">Absolute or relative path to save the configuration</param>
    /// <param name="jsonOptions">JSON serializer options</param>
    public void Save(string path = null, JsonSerializerOptions jsonOptions = null)
    {
        if (path == null)
        {
            if (string.IsNullOrEmpty(LoadedPath))
                return;

            path = LoadedPath;
        }

        jsonOptions ??= Config.jsonOpts;

        File.WriteAllText(path, JsonSerializer.Serialize(this, jsonOptions));
    }
}

