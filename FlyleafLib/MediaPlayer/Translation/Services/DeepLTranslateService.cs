using System.Threading;
using System.Threading.Tasks;
using DeepL;
using DeepL.Model;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class DeepLTranslateService : ITranslateService
{
    private string? _srcLang;
    private string? _targetLang;
    private readonly Translator _translator;
    private readonly DeepLTranslateSettings _settings;

    public DeepLTranslateService(DeepLTranslateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new TranslationConfigException(
                $"API Key for {ServiceType} is not configured.");
        }

        _settings = settings;
        _translator = new Translator(_settings.ApiKey, new TranslatorOptions()
        {
            OverallConnectionTimeout = TimeSpan.FromMilliseconds(settings.TimeoutMs)
        });
    }

    public TranslateServiceType ServiceType => TranslateServiceType.DeepL;

    public void Dispose()
    {
        _translator.Dispose();
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        (TranslateLanguage srcLang, _) = this.TryGetLanguage(src, target);

        _srcLang = ToSourceCode(srcLang.ISO6391);
        _targetLang = ToTargetCode(target);
    }

    internal static string ToSourceCode(string iso6391)
    {
        // ref: https://developers.deepl.com/docs/resources/supported-languages

        // Just capitalize ISO6391.
        return iso6391.ToUpper();
    }

    internal static string ToTargetCode(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.EnglishAmerican => "EN-US",
            TargetLanguage.EnglishBritish => "EN-GB",
            TargetLanguage.Portuguese => "PT-PT",
            TargetLanguage.PortugueseBrazilian => "PT-BR",
            TargetLanguage.ChineseSimplified => "ZH-HANS",
            TargetLanguage.ChineseTraditional => "ZH-HANT",
            _ => target.ToISO6391().ToUpper()
        };
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        if (_srcLang == null || _targetLang == null)
            throw new InvalidOperationException("must be initialized");

        try
        {
            TextResult result = await _translator.TranslateTextAsync(text, _srcLang, _targetLang,
                new TextTranslateOptions
                {
                    Formality = Formality.Default,
                }, token).ConfigureAwait(false);

            return result.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeout: DeepL.ConnectionException
            throw new TranslationException($"Cannot request to {ServiceType}: {ex.Message}", ex);
        }
    }
}
