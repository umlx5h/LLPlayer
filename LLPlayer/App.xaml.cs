using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;

namespace LLPlayer;

public partial class App : PrismApplication
{
    public static string Name => "LLPlayer";
    public static string? CmdUrl { get; private set; } = null;
    public static string PlayerConfigPath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LLPlayer.PlayerConfig.json");
    public static string EngineConfigPath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LLPlayer.Engine.json");
    public static string AppConfigPath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LLPlayer.Config.json");

    private readonly LogHandler Log;

    public App()
    {
        Log = new LogHandler("[App] [MainApp       ] ");
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry
            .Register<Player>(FlyleafLoader.CreateFlyleafPlayer)
            .RegisterSingleton<FlyleafManager>()
            .RegisterSingleton<IDialogService, ExtendedDialogService>();

        containerRegistry.RegisterDialogWindow<MyDialogWindow>();

        containerRegistry.RegisterDialog<SettingsDialog>();
        containerRegistry.RegisterDialog<SubtitlesDownloaderDialog>();
        containerRegistry.RegisterDialog<CheatSheetDialog>();
        containerRegistry.RegisterDialog<WhisperDownloadDialog>();
        containerRegistry.RegisterDialog<TesseractDownloadDialog>();
        containerRegistry.RegisterDialog<ErrorDialog>();
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 1)
        {
            CmdUrl = e.Args[0];
        }

        // Set thread culture to English and error messages to English
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

        // TODO: L: customizable?
        // Ensures that we have enough worker threads to avoid the UI from freezing or not updating on time
        ThreadPool.GetMinThreads(out int workers, out int ports);
        ThreadPool.SetMinThreads(workers + 6, ports + 6);

        // Start flyleaf engine
        FlyleafLoader.StartEngine();

        base.OnStartup(e);
    }

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Ignore WPF Clipboard exception
        if (e.Exception is COMException { ErrorCode: -2147221040 })
        {
            e.Handled = true;
        }

        if (!e.Handled)
        {
            Log.Error($"Unknown error occurred in App: {e.Exception}");

            ErrorDialogHelper.ShowUnknownErrorPopup($"Unhandled Exception: {e.Exception.Message}", "Global", e.Exception);
            e.Handled = true;
        }
    }

    #region App Version
    private static string? _version;
    public static string Version
    {
        get
        {
            if (_version == null)
            {
                (_version, _commitHash) = GetVersion();
            }

            return _version;
        }
    }

    private static string? _commitHash;
    public static string CommitHash
    {
        get
        {
            if (_commitHash == null)
            {
                (_version, _commitHash) = GetVersion();
            }

            return _commitHash;
        }
    }

    private static (string version, string commitHash) GetVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);

        Guards.ThrowIfNull(fvi.ProductVersion);

        var version = fvi.ProductVersion.Split("+");
        if (version.Length != 2)
        {
            throw new InvalidOperationException($"ProductVersion is invalid: {fvi.ProductVersion}");
        }

        return (version[0], version[1]);
    }
    #endregion
}
