using System.IO;
using System.Windows;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaPlayer.Translation;

namespace LLPlayer.Services;

public static class FlyleafLoader
{
    public static void StartEngine()
    {
        EngineConfig engineConfig = DefaultEngineConfig();

        // Load Player's Config
        if (File.Exists(App.EngineConfigPath))
        {
            try
            {
                var opts = AppConfig.GetJsonSerializerOptions();
                engineConfig = EngineConfig.Load(App.EngineConfigPath, opts);
                if (engineConfig.Version != App.Version)
                {
                    engineConfig.Version = App.Version;
                    engineConfig.Save(App.EngineConfigPath, opts);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot load EngineConfig from {Path.GetFileName(App.EngineConfigPath)}, Please review the settings or delete the config file. Error details are recorded in {Path.GetFileName(App.CrashLogPath)}.");
                try
                {
                    File.WriteAllText(App.CrashLogPath, "EngineConfig Loading Error: " + ex);
                }
                catch
                {
                    // ignored
                }

                Application.Current.Shutdown();
            }
        }

        Engine.Start(engineConfig);
    }

    public static Player CreateFlyleafPlayer()
    {
        Config? config = null;
        bool useConfig = false;

        // Load Player's Config
        if (File.Exists(App.PlayerConfigPath))
        {
            try
            {
                var opts = AppConfig.GetJsonSerializerOptions();
                config = Config.Load(App.PlayerConfigPath, opts);

                if (config.Version != App.Version)
                {
                    config.Version = App.Version;
                    config.Save(App.PlayerConfigPath, opts);
                }
                useConfig = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot load PlayerConfig from {Path.GetFileName(App.PlayerConfigPath)}, Please review the settings or delete the config file. Error details are recorded in {Path.GetFileName(App.CrashLogPath)}.");
                try
                {
                    File.WriteAllText(App.CrashLogPath, "PlayerConfig Loading Error: " + ex);
                }
                catch
                {
                    // ignored
                }

                Application.Current.Shutdown();
            }
        }

        config ??= DefaultConfig();
        Player player = new(config);

        if (!useConfig)
        {
            // Initialize default key bindings for custom keys for new config.
            foreach (var binding in AppActions.DefaultCustomActionsMap())
            {
                config.Player.KeyBindings.Keys.Add(binding);
            }
        }

        return player;
    }

    public static EngineConfig DefaultEngineConfig()
    {
        EngineConfig engineConfig = new()
        {
#if DEBUG
            PluginsPath = @":Plugins\bin\Plugins.NET10",
#else
            PluginsPath = ":Plugins",
#endif
            FFmpegPath = ":FFmpeg",
            FFmpegHLSLiveSeek = true,
            UIRefresh = true,
            FFmpegLoadProfile = Flyleaf.FFmpeg.LoadProfile.Filters,
#if DEBUG
            LogOutput = ":debug",
            LogLevel = LogLevel.Debug,
            FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warn,
#endif
        };

        return engineConfig;
    }

    private static Config DefaultConfig() => new()
    {
        Demuxer =
        {   // NOTE: Same Dictionary Ref for all
            FormatOpt              = defaultFormatOpt,
            AudioFormatOpt         = defaultFormatOpt,
            SubtitlesFormatOpt     = defaultFormatOpt,
            FormatOptToUnderlying  = true, // Mainly for HLS to pass the original query which might includes session keys
        },
        Video =
        {
            GPUAdapter = "", // Set it empty so it will include it when we save it
        },
        Subtitles =
        {
            SearchLocal = true,
            TranslateTargetLanguage = Language.Get(Utils.OriginalCulture).ToTargetLanguage() ?? TargetLanguage.EnglishAmerican, // try to set native language
        },
    };

    static readonly Dictionary<string, string> defaultFormatOpt = DefaultFormatOpt();
    static Dictionary<string, string> DefaultFormatOpt()
    {
        var opts = Config.DemuxerConfig.DefaultFormatOpt();

        opts["probesize"]       = $"{50L * 1024 * 1024}";
        opts["analyzeduration"] = $"{TimeSpan.FromSeconds(10).TotalMicroseconds}";
        opts["extension_picky"] = "0";
        opts["rtsp_transport"]  = "tcp";

        return opts;
    }
}
