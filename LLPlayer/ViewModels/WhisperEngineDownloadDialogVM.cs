using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FlyleafLib;
using LLPlayer.Extensions;
using SevenZip;
using File = System.IO.File;

namespace LLPlayer.ViewModels;

public class WhisperEngineDownloadDialogVM : Bindable, IDialogAware
{
    // currently not reusable at all
    public static string EngineURL => "https://github.com/Purfview/whisper-standalone-win/releases/tag/Faster-Whisper-XXL";
    public static string EngineFile => "Faster-Whisper-XXL_r245.3_windows.7z";
    private static string EngineDownloadURL =
        "https://github.com/umlx5h/LLPlayer/releases/download/v0.0.1/Faster-Whisper-XXL_r245.3_windows.7z";
    private static string EngineName = "Faster-Whisper-XXL";
    private static string EnginePath = Path.Combine(WhisperConfig.EnginesDirectory, EngineName);

    public WhisperEngineDownloadDialogVM()
    {
        CmdDownloadEngine.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(CmdDownloadEngine.IsExecuting))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanDelete));
            }
        };
    }

    public string StatusText { get; set => Set(ref field, value); }

    public long DownloadedSize { get; set => Set(ref field, value); }

    public bool Downloaded => Directory.Exists(EnginePath);

    public bool CanDownload => !Downloaded && !CmdDownloadEngine.IsExecuting;

    public bool CanDelete => Downloaded && !CmdDownloadEngine.IsExecuting;

    private CancellationTokenSource? _cts;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public AsyncDelegateCommand CmdDownloadEngine => field ??= new AsyncDelegateCommand(async () =>
    {
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        string tempPath = Path.GetTempPath();
        string tempDownloadFile = Path.Combine(tempPath, EngineFile);

        try
        {
            StatusText = $"Engine '{EngineName}' downloading..";

            await DownloadEngineWithProgressAsync(EngineDownloadURL, tempDownloadFile, token);

            StatusText = $"Engine '{EngineName}' unzipping..";
            await UnzipEngine(tempDownloadFile);

            StatusText = $"Engine '{EngineName}' is downloaded successfully";
            OnDownloadStatusChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download canceled";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to download: {ex.Message}";
        }
        finally
        {
            _cts = null;
            DeleteTempEngine();
        }

        return;

        bool DeleteTempEngine()
        {
            // Delete temporary files if they exist
            if (File.Exists(tempDownloadFile))
            {
                try
                {
                    File.Delete(tempDownloadFile);
                }
                catch (Exception ex)
                {
                    // ignore

                    return false;
                }
            }

            return true;
        }
    }).ObservesCanExecute(() => CanDownload);

    public DelegateCommand CmdCancelDownloadEngine => field ??= new(() =>
    {
        _cts?.Cancel();
    });

    public AsyncDelegateCommand CmdDeleteEngine => field ??= new AsyncDelegateCommand(async () =>
    {
        try
        {
            StatusText = $"Engine '{EngineName}' deleting...";

            // Delete engine if exists
            if (Directory.Exists(EnginePath))
            {
                await Task.Run(() =>
                {
                    Directory.Delete(EnginePath, true);
                });
            }

            OnDownloadStatusChanged();

            StatusText = $"Engine '{EngineName}' is deleted successfully";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to delete engine: {ex.Message}";
        }
    }).ObservesCanExecute(() => CanDelete);

    public DelegateCommand CmdOpenFolder => field ??= new(() =>
    {
        if (!Directory.Exists(WhisperConfig.EnginesDirectory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = WhisperConfig.EnginesDirectory,
            UseShellExecute = true,
            CreateNoWindow = true
        });
    });
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    private void OnDownloadStatusChanged()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
    }

    private async Task UnzipEngine(string zipPath)
    {
        WhisperConfig.EnsureEnginesDirectory();

        SevenZipBase.SetLibraryPath("lib/7z.dll");

        using (SevenZipExtractor extractor = new(zipPath))
        {
            await extractor.ExtractArchiveAsync(WhisperConfig.EnginesDirectory);
        }

        string licencePath = Path.Combine(WhisperConfig.EnginesDirectory, "license.txt");

        if (File.Exists(licencePath) && Directory.Exists(WhisperConfig.EnginesDirectory))
        {
            // move license.txt to engine directory
            File.Move(licencePath, Path.Combine(EnginePath, "license.txt"));
        }
    }

    private async Task<long> DownloadEngineWithProgressAsync(string url, string destinationPath, CancellationToken token)
    {
        DownloadedSize = 0;

        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

        response.EnsureSuccessStatusCode();

        await using Stream engineStream = await response.Content.ReadAsStreamAsync(token);
        await using FileStream fileWriter = File.Open(destinationPath, FileMode.Create);

        byte[] buffer = new byte[1024 * 128];
        int bytesRead;
        long totalBytesRead = 0;

        Stopwatch sw = new();
        sw.Start();

        while ((bytesRead = await engineStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
        {
            await fileWriter.WriteAsync(buffer, 0, bytesRead, token);
            totalBytesRead += bytesRead;

            if (sw.Elapsed > TimeSpan.FromMilliseconds(50))
            {
                DownloadedSize = totalBytesRead;
                sw.Restart();
            }

            token.ThrowIfCancellationRequested();
        }

        return totalBytesRead;
    }

    #region IDialogAware
    public string Title { get; set => Set(ref field, value); } = $"Whisper Engine Downloader - {App.Name}";
    public double WindowWidth { get; set => Set(ref field, value); } = 400;
    public double WindowHeight { get; set => Set(ref field, value); } = 210;

    public bool CanCloseDialog() => !CmdDownloadEngine.IsExecuting;
    public void OnDialogClosed() { }
    public void OnDialogOpened(IDialogParameters parameters) { }
    public DialogCloseListener RequestClose { get; }
    #endregion IDialogAware
}
