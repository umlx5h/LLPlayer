using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlyleafLib.MediaPlayer.Translation.Services;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer.Translation;

#nullable enable

public class SubTranslator
{
    private readonly SubManager _subManager;
    private readonly Config.SubtitlesConfig _config;
    private readonly int _subIndex;

    private ITranslateService? _translateService;
    private SemaphoreSlim? _concurrentLimiter;
    private CancellationTokenSource? _translationCancellation;
    private readonly TranslateServiceFactory _translateServiceFactory;
    private Task? _translateTask;
    private bool _isReset;
    private bool IsEnabled => _config[_subIndex].EnabledTranslated;

    private readonly LogHandler Log;

    public SubTranslator(SubManager subManager, Config.SubtitlesConfig config, int subIndex)
    {
        _subManager = subManager;
        _config = config;
        _subIndex = subIndex;

        _subManager.PropertyChanged += SubManager_OnPropertyChanged;

        // apply config changes
        _config.PropertyChanged += SubtitlesConfig_OnPropertyChanged;
        _config.SubConfigs[subIndex].PropertyChanged += SubConfig_OnPropertyChanged;
        _config.TranslateChatConfig.PropertyChanged += TranslateChatConfig_OnPropertyChanged;

        _translateServiceFactory = new TranslateServiceFactory(config);

        Log = new LogHandler(("[#1]").PadRight(8, ' ') + $" [Translator{subIndex + 1}   ] ");
    }

    private int _oldIndex = -1;
    private CancellationTokenSource? _translationStartCancellation;

    private void SubManager_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SubManager.CurrentIndex):
                if (!IsEnabled || _subManager.Subs.Count == 0 || _subManager.Language == null)
                    return;

                if (_translationStartCancellation != null)
                {
                    _translationStartCancellation.Cancel();
                    _translationStartCancellation.Dispose();
                    _translationStartCancellation = null;
                }
                _translationStartCancellation = new CancellationTokenSource();
                _ = UpdateCurrentIndexAsync(_subManager.CurrentIndex, _translationStartCancellation.Token);
                break;
            case nameof(SubManager.Language):
                _ = Reset();
                break;
        }
    }

    private void SubtitlesConfig_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string languageFallbackPropName = _subIndex == 0 ?
            nameof(Config.SubtitlesConfig.LanguageFallbackPrimary) :
            nameof(Config.SubtitlesConfig.LanguageFallbackSecondary);

        if (e.PropertyName is
            nameof(Config.SubtitlesConfig.TranslateServiceType) or
            nameof(Config.SubtitlesConfig.TranslateTargetLanguage) or
            nameof(Config.SubtitlesConfig.TranslateMaxConcurrent)
            ||
            e.PropertyName == languageFallbackPropName)
        {
            // Apply translating config changes
            _ = Reset();
        }
    }

    private void SubConfig_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Reset when translation is off
        if (e.PropertyName is nameof(Config.SubConfig.EnabledTranslated))
        {
            if (!IsEnabled)
            {
                _ = Reset();
            }
        }
    }

    private void TranslateChatConfig_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = Reset();
    }

    private async Task Reset()
    {
        if (_translateService == null)
            return;

        try
        {
            _isReset = true;
            await Cancel();

            _concurrentLimiter = null;
            _translateService?.Dispose();
            _translateService = null;
        }
        finally
        {
            _isReset = false;
        }
    }

    private async Task Cancel()
    {
        _translationCancellation?.Cancel();
        Task? pendingTask = _translateTask;
        if (pendingTask != null)
        {
            await pendingTask;
        }
        _translateTask = null;
    }

    private async Task UpdateCurrentIndexAsync(int newIndex, CancellationToken token)
    {
        if (newIndex != -1 && _subManager.Subs.Any())
        {
            int indexDiff = Math.Abs(_oldIndex - newIndex);
            _oldIndex = newIndex;

            // Prevent continuous firing when continuously switching subtitles with sub seek
            if (indexDiff == 1)
            {
                if (_subManager.Subs[newIndex].Duration.TotalMilliseconds > 320)
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();

            if (_translateTask == null && !_isReset)
            {
                // singleton task
                _translateTask = TranslateAheadAsync(newIndex, _config.TranslateCount);
                _translateTask.ContinueWith((t) =>
                {
                    // clear when completed
                    _translateTask = null;
                });
            }
        }
    }

    private readonly Lock _initLock = new();
    // initialize TranslateService lazily
    private void EnsureTranslationService()
    {
        if (_translateService == null)
        {
            lock (_initLock)
            {
                if (_translateService == null)
                {
                    _translateService = _translateServiceFactory.GetService(_config.TranslateServiceType, false);
                    if (_config.TranslateServiceType.IsLLM() &&
                        _config.TranslateChatConfig.TranslateMethod == ChatTranslateMethod.KeepContext)
                    {
                        // fixed to 1
                        _concurrentLimiter = new SemaphoreSlim(1, 1);
                    }
                    else
                    {
                        _concurrentLimiter = new SemaphoreSlim(_config.TranslateMaxConcurrent, _config.TranslateMaxConcurrent);
                    }
                    _translateService.Initialize(_subManager.Language, _config.TranslateTargetLanguage);
                }
            }
        }
    }

    private async Task TranslateAheadAsync(int currentIndex, int count)
    {
        try
        {
            // Token for canceling translation, releasing previous one to prevent leakage
            _translationCancellation?.Dispose();
            _translationCancellation = new CancellationTokenSource();

            var token = _translationCancellation.Token;
            int start = currentIndex;
            if (start == -1)
            {
                start = 0;
            }

            int end = Math.Min(start + count - 1, _subManager.Subs.Count - 1);

            List<Task> translationTasks = new();

            for (int i = start; i <= end; i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (i >= _subManager.Subs.Count)
                {
                    break;
                }

                var sub = _subManager.Subs[i];

                if (!sub.IsTranslated && !string.IsNullOrWhiteSpace(sub.Text))
                {
                    Task task = TranslateSubAsync(sub, token);
                    translationTasks.Add(task);

                    task.ContinueWith(t =>
                    {
                        // If one error occurs, cancel other requests for immediate exit
                        _translationCancellation?.Cancel();
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            if (translationTasks.Any())
            {
                await Task.WhenAll(translationTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Log.Error($"Translation failed: {ex.Message}");

            bool isConfigError = ex is TranslationConfigException;

            // Unable to translate, so turn off the translation and notify
            _config[_subIndex].EnabledTranslated = false;
            Reset().ContinueWith((_) =>
            {
                if (isConfigError)
                {
                    _config.player.RaiseKnownErrorOccurred($"Translation Failed: {ex.Message}", KnownErrorType.Configuration);
                }
                else
                {
                    _config.player.RaiseUnknownErrorOccurred($"Translation Failed: {ex.Message}", UnknownErrorType.Translation, ex);
                }
            });
        }
    }

    private async Task TranslateSubAsync(SubtitleData sub, CancellationToken token)
    {
        EnsureTranslationService();
        await _concurrentLimiter!.WaitAsync(token).ConfigureAwait(false);

        try
        {
            long start = Stopwatch.GetTimestamp();
            string text = sub.Text!.ReplaceLineEndings(" ");
            if (CanDebug) Log.Debug($"Translation Start {sub.Index} - {text}");
            string translated = await _translateService!.TranslateAsync(text, token).ConfigureAwait(false);
            sub.TranslatedText = translated;

            if (CanDebug)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                Log.Debug($"Translation End {sub.Index} in {elapsed.TotalMilliseconds} - {translated}");
            }
        }
        catch (OperationCanceledException)
        {
            if (CanDebug) Log.Debug($"Translation Cancel {sub.Index}");
            throw;
        }
        finally
        {
            _concurrentLimiter?.Release();
        }
    }
}
