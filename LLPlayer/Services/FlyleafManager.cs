using System.IO;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

namespace LLPlayer.Services;

public class FlyleafManager
{
    public Player Player { get; }
    public Config PlayerConfig => Player.Config;
    public FlyleafHost? FlyleafHost => Player.Host as FlyleafHost;
    public AppConfig Config { get; }
    public AudioEngine AudioEngine => Engine.Audio;
    public EngineConfig ConfigEngine => Engine.Config;

    public FlyleafManager(Player player, IDialogService dialogService)
    {
        Player = player;

        // Load app configuration at this time
        Config = LoadAppConfig();
    }

    private AppConfig LoadAppConfig()
    {
        AppConfig? config = null;

        if (File.Exists(App.AppConfigPath))
        {
            try
            {
                config = AppConfig.Load(App.AppConfigPath);
            }
            catch
            {
                // ignored
                // TODO: L: error handling
                // If it was an incorrect configuration file and it will be set to the default settings, the current settings will be lost.
            }
        }

        if (config == null)
        {
            config = new AppConfig();
        }
        config.Initialize(this);

        return config;
    }
}
