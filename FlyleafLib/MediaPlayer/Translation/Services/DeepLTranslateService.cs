using DeepL;
using DeepL.Model;
using System.Threading;
using System.Threading.Tasks;

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
        _settings = settings;
        _translator = new Translator(settings.ApiKey);
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        string iso6391 = src.ISO6391;

        if (src == Language.Unknown)
        {
            throw new ArgumentException("src language are unknown");
        }

        // Exception for same language
        if (src.ISO6391 == target.ToISO6391())
        {
            throw new ArgumentException("src and target language are same");
        }

        if (!TranslateLanguage.Langs.TryGetValue(iso6391, out var srcLang))
        {
            throw new ArgumentException($"src language is not supported: {src}", nameof(src));
        }

        if (!srcLang.SupportedServices.HasFlag(TranslateServiceType.DeepL))
        {
            throw new ArgumentException($"src language is not supported by DeepL: {src}", nameof(src));
        }

        _srcLang = ToSourceCode(srcLang.ISO6391);

        if (!TranslateLanguage.Langs.TryGetValue(target.ToISO6391(), out var targetLang))
        {
            throw new ArgumentException($"target language is not supported: {target}", nameof(target));
        }

        if (!targetLang.SupportedServices.HasFlag(TranslateServiceType.DeepL))
        {
            throw new ArgumentException($"target language is not supported by DeepL: {target}", nameof(target));
        }

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
            TextResult result = await _translator.TranslateTextAsync(text, _srcLang, _targetLang, new TextTranslateOptions
            {
                Formality = Formality.Default,
            }, token);

            return result.Text;
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Cannot translate with DeepL: {ex.Message}", ex);
        }
    }
}
