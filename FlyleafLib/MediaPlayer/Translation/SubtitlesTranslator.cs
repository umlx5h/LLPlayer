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
            nameof(Config.SubtitlesConfig.TranslateTargetLanguage)
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
            bool isForward = newIndex > _oldIndex;
            _oldIndex = newIndex;

            if ((isForward && indexDiff > _config.TranslateCountForward) ||
                (!isForward && indexDiff > _config.TranslateCountBackward))
            {
                // Cancel a running translation request when a large seek is performed
                await Cancel();
            }
            else if (_translateTask != null)
            {
                // for performance
                return;
            }

            // Prevent continuous firing when continuously switching subtitles with sub seek
            if (indexDiff == 1)
            {
                if (_subManager.Subs[newIndex].Duration.TotalMilliseconds > 320)
                {
                    await Task.Delay(300, token);
                }
            }
            token.ThrowIfCancellationRequested();

            if (_translateTask == null && !_isReset)
            {
                // singleton task
                // Ensure that it is not executed in the main thread because it scans all subtitles
                Task task = Task.Run(async () =>
                {
                    try
                    {
                        await TranslateAheadAsync(newIndex, _config.TranslateCountBackward, _config.TranslateCountForward);
                    }
                    finally
                    {
                        _translateTask = null;
                    }
                });
                _translateTask = task;
            }
        }
    }

    private readonly Lock _initLock = new();
    // initialize TranslateService lazily
    private void EnsureTranslationService()
    {
        if (_translateService != null)
        {
            return;
        }

        // double-check lock pattern
        lock (_initLock)
        {
            if (_translateService == null)
            {
                var service = _translateServiceFactory.GetService(_config.TranslateServiceType, false);
                service.Initialize(_subManager.Language, _config.TranslateTargetLanguage);

                Volatile.Write(ref _translateService, service);
            }
        }
    }

    private async Task TranslateAheadAsync(int currentIndex, int countBackward, int countForward)
    {
        try
        {
            // Token for canceling translation, releasing previous one to prevent leakage
            _translationCancellation?.Dispose();
            _translationCancellation = new CancellationTokenSource();

            var token = _translationCancellation.Token;
            int start = Math.Max(0, currentIndex - countBackward);
            int end = Math.Min(start + countForward - 1, _subManager.Subs.Count - 1);

            List<SubtitleData> translateSubs = new();
            for (int i = start; i <= end; i++)
            {
                if (token.IsCancellationRequested)
                    break;
                if (i >= _subManager.Subs.Count)
                    break;

                var sub = _subManager.Subs[i];
                if (!sub.IsTranslated && !string.IsNullOrEmpty(sub.Text))
                {
                    translateSubs.Add(sub);
                }
            }

            if (translateSubs.Count == 0)
                return;

            int concurrency = _config.TranslateMaxConcurrency;

            if (concurrency > 1 && _config.TranslateServiceType.IsLLM() &&
                _config.TranslateChatConfig.TranslateMethod == ChatTranslateMethod.KeepContext)
            {
                // fixed to 1
                // it must be sequential because of maintaining context
                concurrency = 1;
            }

            if (concurrency <= 1)
            {
                // sequentially (important to maintain execution order for LLM)
                foreach (var sub in translateSubs)
                {
                    await TranslateSubAsync(sub, token);
                }
            }
            else
            {
                // concurrently
                ParallelOptions parallelOptions = new()
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = concurrency
                };

                await Parallel.ForEachAsync(
                    translateSubs,
                    parallelOptions,
                    async (sub, ct) =>
                    {
                        await TranslateSubAsync(sub, ct);
                    });
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
        string? text = sub.Text;
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            long start = Stopwatch.GetTimestamp();
            string translateText = SubtitleTextUtil.FlattenText(text);
            if (CanDebug) Log.Debug($"Translation Start {sub.Index} - {translateText}");
            EnsureTranslationService();
            string translated = await _translateService!.TranslateAsync(translateText, token);
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
    }
}
