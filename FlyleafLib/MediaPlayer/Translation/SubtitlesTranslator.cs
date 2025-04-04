using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlyleafLib.MediaPlayer.Translation.Services;

namespace FlyleafLib.MediaPlayer.Translation;

#nullable enable

public class SubTranslator
{
    private readonly SubManager _subManager;
    private readonly Config.SubtitlesConfig _config;
    private readonly int _subIndex;

    private readonly HashSet<int> _translationInProgress = new();
    private readonly SemaphoreSlim _translationSemaphore = new(1);
    private SemaphoreSlim? _concurrentLimiter;
    private ITranslateService? _translateService;
    private CancellationTokenSource? _translationCancellation;
    private readonly TranslateServiceFactory _translateServiceFactory;
    private bool IsEnabled => _config[_subIndex].EnabledTranslated;

    private readonly LogHandler Log;

    public SubTranslator(SubManager subManager, Config.SubtitlesConfig config, int subIndex)
    {
        _subManager = subManager;
        _config = config;
        _subIndex = subIndex;

        _subManager.PropertyChanged += SubManager_OnPropertyChanged;
        _config.PropertyChanged += Config_OnPropertyChanged;

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
                if (_translationStartCancellation != null)
                {
                    _translationStartCancellation.Cancel();
                    _translationStartCancellation.Dispose();
                    _translationStartCancellation = null;
                }
                _translationStartCancellation = new CancellationTokenSource();
                _ = UpdateCurrentIndexAsync(_subManager.CurrentIndex, _translationStartCancellation.Token)
                    .ContinueWith(t =>
                    {
                        if (t.IsCanceled)
                        {
                            Log.Info("Translation start canceled");
                        }
                        else if (t.IsFaulted)
                        {
                            var ex = t.Exception.Flatten().InnerException;
                            Log.Error($"Translation start failed: {ex?.Message}");
                        }
                    }, TaskContinuationOptions.NotOnRanToCompletion);

                break;
            case nameof(SubManager.Language):
                _ = Reset();
                break;
        }
    }

    private void Config_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string languageFallbackPropName = _subIndex == 0 ?
            nameof(Config.SubtitlesConfig.LanguageFallbackPrimary) :
            nameof(Config.SubtitlesConfig.LanguageFallbackSecondary);

        if (e.PropertyName is
            nameof(Config.SubtitlesConfig.TranslateServiceType) or
            nameof(Config.SubtitlesConfig.TranslateTargetLanguage) ||
            e.PropertyName == languageFallbackPropName)
        {
            // Apply translating config changes
            _ = Reset();
        }
    }

    public async Task Reset()
    {
        try
        {
            _translationCancellation?.Cancel();
            _translationCancellation?.Dispose();
        }
        catch
        {
            // ignored
        }

        // Wait until it is not used before setting it to null.
        await _translationSemaphore.WaitAsync();
        _translateService?.Dispose();
        _translateService = null;
        _translationSemaphore.Release();
    }

    public async Task UpdateCurrentIndexAsync(int newIndex, CancellationToken token)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (newIndex != -1 && _subManager.Subs.Any())
        {
            // Prevent continuous firing when continuously switching subtitles with sub seek
            if (Math.Abs(_oldIndex - newIndex) == 1)
            {
                _oldIndex = newIndex;

                if (_subManager.Subs[newIndex].Duration.TotalMilliseconds > 320)
                {
                    await Task.Delay(300, token);
                }
            }
            _oldIndex = newIndex;
            token.ThrowIfCancellationRequested();

            await TranslateAheadAsync(newIndex, _config.TranslateCount);
        }
    }

    // initialize TranslateService lazily
    private async Task<bool> InitializeTranslationService(Language srcLang)
    {
        try
        {
            await _translationSemaphore.WaitAsync();

            if (_translateService != null)
                return true;

            try
            {
                _translateService = _translateServiceFactory.GetService(_config.TranslateServiceType, false);
                if (_config.TranslateServiceType.IsLLM() &&
                    _config.TranslateChatConfig.TranslateMethod == ChatTranslateMethod.KeepContext)
                {
                    // fixed to 1
                    _concurrentLimiter = new SemaphoreSlim(1);
                }
                else
                {
                    _concurrentLimiter = new SemaphoreSlim(_config.TranslateMaxConcurrent);
                }
                _translateService.Initialize(srcLang, _config.TranslateTargetLanguage);
            }
            catch (TranslationConfigException ex)
            {
                // Unable to translate, so turn off the translation and notify
                _ = Reset().ContinueWith((_) =>
                {
                    _config[_subIndex].EnabledTranslated = false;
                    _config.player.RaiseKnownErrorOccurred(ex.Message, KnownErrorType.Configuration);
                });

                return false;
            }

            return true;
        }
        finally
        {
            _translationSemaphore.Release();
        }

    }

    // TODO: L: Is it possible to retain the context of the previous subtitle and translate it?
    // https://stackoverflow.com/questions/41169814/translating-parts-of-sentences-based-on-its-context
    private async Task TranslateAheadAsync(int currentIndex, int count)
    {
        var srcLang = _subManager.Language;
        if (_subManager.Subs.Count == 0 || srcLang == null)
        {
            return;
        }

        if (_translateService == null)
        {
            if (!await InitializeTranslationService(srcLang))
            {
                return;
            }
        }

        try
        {
            await _translationSemaphore.WaitAsync();

            try
            {
                _translationCancellation?.Cancel();
                _translationCancellation?.Dispose();
            }
            catch
            {
                // ignored
            }

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
                    return;
                }

                // TODO: L: Out of bounds may occur at this point, for example, at the end of the session (because the lock is not taken).k
                if (i >= _subManager.Subs.Count)
                {
                    break;
                }

                var sub = _subManager.Subs[i];

                if (_translationInProgress.Contains(sub.Index))
                {
                    // running translation
                    return;
                }

                if (!string.IsNullOrWhiteSpace(sub.Text) && !sub.IsTranslated)
                {
                    _translationInProgress.Add(sub.Index);
                    await _concurrentLimiter!.WaitAsync(token);

                    Task task = TranslateSubAsync(sub, token).ContinueWith((_) =>
                    {
                        _translationInProgress.Remove(sub.Index);
                        _concurrentLimiter.Release();
                    }, token);
                    translationTasks.Add(task);
                }
            }

            if (translationTasks.Any())
            {
                await Task.WhenAll(translationTasks);
            }
        }

        finally
        {
            _translationSemaphore.Release();
        }
    }

    private async Task TranslateSubAsync(SubtitleData sub, CancellationToken token)
    {
        if (_translateService == null)
            return;

        try
        {
            long start = Stopwatch.GetTimestamp();
            string text = sub.Text!.ReplaceLineEndings(" ");
            string translated = await _translateService.TranslateAsync(text, token);
            sub.TranslatedText = translated;

            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            Log.Debug($"Translation {sub.Index} in {elapsed.TotalMilliseconds} - {translated}");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Translation canceled");
            throw;
        }
        catch (Exception ex)
        {
            // Unable to translate, so turn off the translation and notify
            _ = Reset().ContinueWith((_) =>
            {
                _config[_subIndex].EnabledTranslated = false;

                _config.player.RaiseUnknownErrorOccurred(ex.Message, UnknownErrorType.Translation, ex);
            });

            Log.Error($"Translation failed for index {sub.Index}: {ex.Message}");
        }
    }
}
