namespace FlyleafLib;

public class FFmpegEngine
{
    public string   Folder          { get; private set; }
    public string   Version         { get; private set; }

    public bool     FiltersLoaded   { get; set; }
    public bool     DevicesLoaded   { get; set; }

    const int           AV_LOG_BUFFER_SIZE = 5 * 1024;
    internal AVRational AV_TIMEBASE_Q;

    internal FFmpegEngine()
    {
        try
        {
            Engine.Log.Info($"Loading FFmpeg libraries from '{Engine.Config.FFmpegPath}'");
            Folder = Utils.GetFolderPath(Engine.Config.FFmpegPath);
            LoadLibraries(Folder, Engine.Config.FFmpegDevices ? LoadProfile.All : LoadProfile.Filters);

            FiltersLoaded = true; // Possible allow only main profile?
            DevicesLoaded = Engine.Config.FFmpegDevices;

            uint ver = avformat_version();
            Version = $"{ver >> 16}.{(ver >> 8) & 255}.{ver & 255}";

            SetLogLevel();
            AV_TIMEBASE_Q   = av_get_time_base_q();
            Engine.Log.Info($"FFmpeg Loaded (Location: {Folder}, Ver: {Version}) [Devices: {(DevicesLoaded ? "yes" : "no")}, Filters: {(FiltersLoaded ? "yes" : "no")}]");
        } catch (Exception e)
        {
            Engine.Log.Error($"Loading FFmpeg libraries '{Engine.Config.FFmpegPath}' failed\r\n{e.Message}\r\n{e.StackTrace}");
            throw new Exception($"Loading FFmpeg libraries '{Engine.Config.FFmpegPath}' failed");
        }
    }

    internal static void SetLogLevel()
    {
        if (Engine.Config.FFmpegLogLevel != Flyleaf.FFmpeg.LogLevel.Quiet)
        {
            av_log_set_level(Engine.Config.FFmpegLogLevel);
            av_log_set_callback(LogFFmpeg);
        }
        else
        {
            av_log_set_level(Flyleaf.FFmpeg.LogLevel.Quiet);
            av_log_set_callback(null);
        }
    }

    internal unsafe static av_log_set_callback_callback LogFFmpeg = (p0, level, format, vl) =>
    {
        if (level > av_log_get_level())
            return;

        byte*   buffer = stackalloc byte[AV_LOG_BUFFER_SIZE];
        int     printPrefix = 1;
        av_log_format_line2(p0, level, format, vl, buffer, AV_LOG_BUFFER_SIZE, &printPrefix);
        string  line = Utils.BytePtrToStringUTF8(buffer);

        Logger.Output($"FFmpeg|{level,-7}|{line.Trim()}");
    };

    internal unsafe static string ErrorCodeToMsg(int error)
    {
        byte* buffer = stackalloc byte[AV_LOG_BUFFER_SIZE];
        av_strerror(error, buffer, AV_LOG_BUFFER_SIZE);
        return Utils.BytePtrToStringUTF8(buffer);
    }
}
