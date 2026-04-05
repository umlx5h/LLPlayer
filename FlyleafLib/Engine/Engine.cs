using System.Windows;

using FlyleafLib.MediaPlayer;

namespace FlyleafLib;

/// <summary>
/// Flyleaf Engine
/// </summary>
public static class Engine
{
    /// <summary>
    /// Engine has been loaded and is ready for use
    /// </summary>
    public static bool              IsLoaded        { get; private set; }

    /// <summary>
    /// Engine's configuration
    /// </summary>
    public static EngineConfig      Config          { get; private set; }

    /// <summary>
    /// Audio Engine
    /// </summary>
    public static AudioEngine       Audio           { get; private set; }

    /// <summary>
    /// Video Engine
    /// </summary>
    public static VideoEngine       Video           { get; private set; }

    /// <summary>
    /// Plugins Engine
    /// </summary>
    public static PluginsEngine     Plugins         { get; private set; }

    /// <summary>
    /// FFmpeg Engine
    /// </summary>
    public static FFmpegEngine      FFmpeg          { get; private set; }

    /// <summary>
    /// List of active Players
    /// </summary>
    public static List<Player>      Players         { get; private set; } = [];

    public static event EventHandler
                    Loaded;

    internal static LogHandler
                    Log;

    static Thread   tMaster;
    static object   lockEngine = new();
    static bool     isLoading;
    static int      timePeriod;
    static int      threadExecutionState; // ES_CONTINUOUS

    /// <summary>
    /// Initializes Flyleaf's Engine (Must be called from UI thread)
    /// </summary>
    /// <param name="config">Engine's configuration</param>
    public static void Start(EngineConfig config = null) => StartInternal(config);

    /// <summary>
    /// Initializes Flyleaf's Engine Async (Must be called from UI thread)
    /// </summary>
    /// <param name="config">Engine's configuration</param>
    public static void StartAsync(EngineConfig config = null) => StartInternal(config, true);

    /// <summary>
    /// Requests timeBeginPeriod(1)
    /// </summary>
    public static void TimeBeginPeriod1()
    {
        lock (lockEngine)
        {
            timePeriod++;

            if (timePeriod == 1)
            {
                #if DEBUG
                Log.Trace("timeBeginPeriod(1)");
                #endif

                _ = NativeMethods.TimeBeginPeriod(1);
            }
        }
    }

    /// <summary>
    /// Stops previously requested timeBeginPeriod(1)
    /// </summary>
    public static void TimeEndPeriod1()
    {
        lock (lockEngine)
        {
            timePeriod--;

            if (timePeriod == 0)
            {
                #if DEBUG
                Log.Trace("timeEndPeriod(1)");
                #endif

                _ = NativeMethods.TimeEndPeriod(1);
            }
        }
    }

    /// <summary>
    /// Requests SetThreadExecutionState
    /// </summary>
    public static void ThreadExecutionStateBegin()
    {
        lock (lockEngine)
        {
            threadExecutionState++;

            if (threadExecutionState == 1)
            {
                #if DEBUG
                Log.Trace("ThreadExecutionStateBegin");
                #endif

                _ = NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS | NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED | NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED);
            }
        }
    }

    /// <summary>
    /// Stops previously requested SetThreadExecutionState
    /// </summary>
    public static void ThreadExecutionStateEnd()
    {
        lock (lockEngine)
        {
            threadExecutionState--;

            if (threadExecutionState == 0)
            {
                #if DEBUG
                Log.Trace("ThreadExecutionStateEnd");
                #endif

                _ = NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
            }
        }
    }

    private static void StartInternal(EngineConfig config = null, bool async = false)
    {
        if (Application.Current == null)
            _ = new Application();

        UIInvokeIfRequired(() =>
        {
            lock (lockEngine)
            {
                if (isLoading)
                    return;

                isLoading = true;

                Config = config ?? new EngineConfig();

                StartInternalUI();

                if (async)
                    Task.Run(() => StartInternalNonUI());
                else
                    StartInternalNonUI();
            }
        });
    }

    private static void StartInternalUI()
    {
        Application.Current.Exit += (o, e) =>
        {
            Config.UIRefresh = false;
            Config.UIRefreshInterval = 1;

            while (Players.Count != 0)
                Players[0].Dispose();
        };

        SetOutput();
        Log     = new("[FlyleafEngine] ");
        Audio   = new();
        Video   = new();
    }

    private static void StartInternalNonUI()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"FlyleafLib {version.Major }.{version.Minor}.{version.Build}");

        FFmpeg  = new();
        Plugins = new();
        IsLoaded= true;
        Loaded?.Invoke(null, null);

        if (Config.UIRefresh)
            StartThread();
    }
    internal static void AddPlayer(Player player)
    {
        lock (Players)
            Players.Add(player);
    }
    internal static int  GetPlayerPos(int playerId)
    {
        for (int i=0; i<Players.Count; i++)
            if (Players[i].PlayerId == playerId)
                return i;

        return -1;
    }
    internal static void DisposePlayer(Player player)
    {
        if (player == null) return;

        DisposePlayer(player.PlayerId);
    }
    internal static void DisposePlayer(int playerId)
    {
        lock (Players)
        {
            Log.Trace($"Disposing {playerId}");
            int pos = GetPlayerPos(playerId);
            if (pos == -1) return;

            var player = Players[pos];
            player.DisposeInternal();
            Players.RemoveAt(pos);
            Log.Trace($"Disposed {playerId}");
        }
    }

    internal static void StartThread()
    {
        if (!Config.UIRefresh || (tMaster != null && tMaster.IsAlive))
            return;

        tMaster = new Thread(() => { MasterThread(); })
        {
            Name = "FlyleafEngine",
            IsBackground = true
        };
        tMaster.Start();
    }
    internal static void MasterThread()
    {
        // TBR: Auto Stop/Start instead of UIRefresh config (based on current Players status/mode/bufferduration/stats etc)
        Log.Info("Thread started");

        int curLoop     = 0;
        int secondLoops = 1000 / Config.UIRefreshInterval;
        long prevTicks  = DateTime.UtcNow.Ticks;
        double curSecond= 0;

        do
        {
            try
            {
                if (Players.Count == 0)
                {
                    Thread.Sleep(Config.UIRefreshInterval);
                    continue;
                }

                curLoop++;
                if (curLoop == secondLoops)
                {
                    long curTicks = DateTime.UtcNow.Ticks;
                    curSecond = (curTicks - prevTicks) / 10000000.0;
                    prevTicks = curTicks;
                }

                lock (Players)
                    foreach (var player in Players)
                    {
                        /* Every UIRefreshInterval */
                        player.Activity.RefreshMode();

                        /* Every Second */
                        if (curLoop == secondLoops) // Calculations here to be second accurate
                        {
                            if (player.Config.Player.Stats)
                            {
                                var curStats        = player.stats;
                                // TODO: L: including Subtitles bytes?
                                long curTotalBytes  = player.VideoDemuxer.TotalBytes + player.AudioDemuxer.TotalBytes;
                                long curVideoBytes  = player.VideoDemuxer.VideoPackets.Bytes + player.AudioDemuxer.VideoPackets.Bytes;
                                long curAudioBytes  = player.VideoDemuxer.AudioPackets.Bytes + player.AudioDemuxer.AudioPackets.Bytes;

                                player.bitRate      = Math.Max(curTotalBytes - curStats.TotalBytes, 0) * 8 / 1000.0;
                                player.Video.bitRate= Math.Max(curVideoBytes - curStats.VideoBytes, 0) * 8 / 1000.0;
                                player.Audio.bitRate= Math.Max(curAudioBytes - curStats.AudioBytes, 0) * 8 / 1000.0;

                                curStats.TotalBytes = curTotalBytes;
                                curStats.VideoBytes = curVideoBytes;
                                curStats.AudioBytes = curAudioBytes;

                                // TBR: Let Fps enable even for Idle
                                //if (player.status == Status.Playing)
                                //{
                                var presentCount = player.Renderer.SwapChain.GetFrameStatistics().PresentCount; // might cause a delay, keep it last
                                player.Video.fpsCurrent  = (presentCount - curStats.FramesDisplayed) / curSecond;
                                curStats.FramesDisplayed = presentCount;
                                //}
                            }
                        }
                    }

                if (curLoop == secondLoops)
                    curLoop = 0;

                void UIAction()
                {
                    try
                    {
                        foreach (var player in Players)
                        {
                            var isPlaying = player.status == Status.Playing;
                            /* Every UIRefreshInterval */
                            var config = player.Config.Player;

                            // Activity Mode Refresh & Hide Mouse Cursor (FullScreen only)
                            if (player.Activity.mode != player.Activity._Mode)
                                player.Activity.SetMode();

                            // Buffered Duration Refresh from Demuxer (TBR: RefreshType?)
                            player.BufferedDuration = player.MainDemuxer.BufferedDuration;

                            // CurTime (PerUIRefreshInterval)
                            if (isPlaying && config.UICurTime == UIRefreshType.PerUIRefreshInterval)
                                player.SetCurTime();

                            /* Every Second */
                            if (curLoop == 0)
                            {
                                // CurTime (PerUISecond)
                                if (config.UICurTime == UIRefreshType.PerUISecond)
                                    player.SetCurTime();

                                // Stats Refresh (BitRates / FrameDisplayed / FramesDropped / FPS)
                                if (config.Stats)
                                {
                                    player.BitRate          = player.BitRate;
                                    player.Video.BitRate    = player.Video.BitRate;
                                    player.Audio.BitRate    = player.Audio.BitRate;

                                    player.Video.FPSCurrent = player.Video.fpsCurrent;

                                    if (isPlaying) // Otherwise Screamers should fire the last update
                                    {
                                        player.Audio.FramesDisplayed= player.Audio.FramesDisplayed;
                                        player.Audio.FramesDropped  = player.Audio.FramesDropped;
                                        (player.Video.FramesDisplayed,player.Video.FramesDropped) = player.FramesDisplayedDropped(); // dynamic update to be closer to 'now'
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                UI(UIAction);
                Thread.Sleep(Config.UIRefreshInterval);

            } catch { curLoop = 0; }

        } while (Config.UIRefresh);

        Log.Info("Thread stopped");
    }
}
