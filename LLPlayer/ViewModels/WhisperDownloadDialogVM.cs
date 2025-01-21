using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;
using Whisper.net.Ggml;

namespace LLPlayer.ViewModels;

public class WhisperDownloadDialogVM : Bindable, IDialogAware
{
    private const string TempExtension = ".tmp";

    public WhisperDownloadDialogVM()
    {
        List<WhisperModel> models = WhisperModelLoader.LoadAllModels();
        foreach (var model in models)
        {
            Models.Add(model);
        }

        SelectedModel = Models.First();

        StatusText = "Select a model to download.";

        CmdDownloadModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(CmdDownloadModel.IsExecuting))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanDelete));
            }
        };
    }

    public ObservableCollection<WhisperModel> Models { get; set => Set(ref field, value); } = new();

    public WhisperModel SelectedModel
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanDelete));
            }
        }
    }

    public string StatusText { get; set => Set(ref field, value); }

    public long DownloadedSize { get; set => Set(ref field, value); }

    public bool CanDownload =>
        SelectedModel is { Downloaded: false } && !CmdDownloadModel.IsExecuting;

    public bool CanDelete =>
        SelectedModel is { Downloaded: true } && !CmdDownloadModel.IsExecuting;

    private CancellationTokenSource? _cts;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public AsyncDelegateCommand CmdDownloadModel => field ??= new AsyncDelegateCommand(async () =>
    {
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        WhisperModel downloadModel = SelectedModel;
        string tempModelPath = downloadModel.ModelFilePath + TempExtension;

        try
        {
            if (downloadModel.Downloaded)
            {
                StatusText = $"Model '{SelectedModel}' is already downloaded";
                return;
            }

            // Delete temporary files if they exist (forces re-download)
            if (!DeleteTempModel())
            {
                StatusText = $"Failed to remove temp model";
                return;
            }

            StatusText = $"Model '{downloadModel}' downloading..";

            long modelSize = await DownloadModelWithProgressAsync(downloadModel.Model, tempModelPath, token);

            // After successful download, rename temporary file to final file]
            File.Move(tempModelPath, downloadModel.ModelFilePath);

            // Update downloaded status
            downloadModel.Size = modelSize;
            OnDownloadStatusChanged();

            StatusText = $"Model '{SelectedModel}' is downloaded successfully";
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
            DeleteTempModel();
        }

        return;

        bool DeleteTempModel()
        {
            // Delete temporary files if they exist
            if (File.Exists(tempModelPath))
            {
                try
                {
                    File.Delete(tempModelPath);
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

    public DelegateCommand CmdCancelDownloadModel => field ??= new(() =>
    {
        _cts?.Cancel();
    });

    public DelegateCommand CmdDeleteModel => field ??= new DelegateCommand(() =>
    {
        try
        {
            StatusText = $"Model '{SelectedModel}' deleting...";

            WhisperModel deleteModel = SelectedModel;

            // Delete model file if exists
            if (File.Exists(deleteModel.ModelFilePath))
            {
                File.Delete(deleteModel.ModelFilePath);
            }

            // Update downloaded status
            deleteModel.Size = 0;
            OnDownloadStatusChanged();

            StatusText = $"Model '{deleteModel}' is deleted successfully";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to download model: {ex.Message}";
        }
    }).ObservesCanExecute(() => CanDelete);

    public DelegateCommand CmdOpenFolder => field ??= new(() =>
    {
        if (!Directory.Exists(WhisperModel.ModelsDirectory))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WhisperModel.ModelsDirectory,
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    private void OnDownloadStatusChanged()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
    }

    private async Task<long> DownloadModelWithProgressAsync(GgmlType modelType, string destinationPath, CancellationToken token)
    {
        DownloadedSize = 0;

        await using Stream modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(modelType, default, token);
        await using FileStream fileWriter = File.OpenWrite(destinationPath);

        byte[] buffer = new byte[1024 * 128];
        int bytesRead;
        long totalBytesRead = 0;

        Stopwatch sw = new();
        sw.Start();

        while ((bytesRead = await modelStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
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
    public string Title { get; set => Set(ref field, value); } = $"Whisper Downloader - {App.Name}";
    public double WindowWidth { get; set => Set(ref field, value); } = 400;
    public double WindowHeight { get; set => Set(ref field, value); } = 200;

    public bool CanCloseDialog() => !CmdDownloadModel.IsExecuting;
    public void OnDialogClosed() { }
    public void OnDialogOpened(IDialogParameters parameters) { }
    public DialogCloseListener RequestClose { get; }
    #endregion IDialogAware
}
