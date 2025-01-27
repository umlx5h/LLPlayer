using System.IO;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

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
                engineConfig = EngineConfig.Load(App.EngineConfigPath, AppConfig.GetJsonSerializerOptions());
            }
            catch
            {
                // ignored
                // TODO: L: error handling
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
                config = Config.Load(App.PlayerConfigPath, AppConfig.GetJsonSerializerOptions());
                useConfig = true;
            }
            catch
            {
                // ignored
                // TODO: L: error handling
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
            PluginsPath = ":Plugins",
            FFmpegPath = ":FFmpeg",
            FFmpegHLSLiveSeek = true,
            UIRefresh = true,
            LogLevel = LogLevel.Debug,
            FFmpegLogLevel = global::Flyleaf.FFmpeg.LogLevel.Warn,
            LogOutput = ":debug"
        };

        return engineConfig;
    }

    private static Config DefaultConfig()
    {
        Config config = new();
        config.Demuxer.FormatOptToUnderlying =
            true; // Mainly for HLS to pass the original query which might includes session keys
        config.Audio.FiltersEnabled = true; // To allow embedded atempo filter for speed
        config.Video.GPUAdapter = ""; // Set it empty so it will include it when we save it
        config.Subtitles.SearchLocal = true;

        // TODO: L: Allow customization in settings
        // Give top most priority to English
        config.Audio.Languages =
            config.Audio.Languages.Take(config.Audio.Languages.Count - 1)
                .Prepend(config.Audio.Languages.Last()).ToList();
        config.Subtitles.Languages =
            config.Subtitles.Languages.Take(config.Subtitles.Languages.Count - 1)
                .Prepend(config.Subtitles.Languages.Last()).ToList();
        return config;
    }
}
