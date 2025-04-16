using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

public interface ITranslateService : IDisposable
{
    TranslateServiceType ServiceType { get; }

    /// <summary>
    /// Initialize
    /// </summary>
    /// <param name="src"></param>
    /// <param name="target"></param>
    /// <exception cref="TranslationConfigException">when language is not supported or configured properly</exception>
    void Initialize(Language src, TargetLanguage target);

    /// <summary>
    /// TranslateAsync
    /// </summary>
    /// <param name="text"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TranslationException">when translation is failed</exception>
    /// <exception cref="OperationCanceledException"></exception>
    Task<string> TranslateAsync(string text, CancellationToken token);
}

[Flags]
public enum TranslateServiceType
{
    /// <summary>
    /// Google V1.
    /// </summary>
    GoogleV1 = 1 << 0,

    /// <summary>
    /// DeepL
    /// </summary>
    DeepL = 1 << 1,

    /// <summary>
    /// DeepLX
    /// https://github.com/OwO-Network/DeepLX
    /// </summary>
    DeepLX = 1 << 2,

    /// <summary>
    /// Ollama
    /// </summary>
    Ollama = 1 << 3,

    /// <summary>
    /// LM Studio
    /// </summary>
    LMStudio = 1 << 4,

    /// <summary>
    /// OpenAI (ChatGPT)
    /// </summary>
    OpenAI = 1 << 5,

    /// <summary>
    /// Anthropic Claude
    /// </summary>
    Claude = 1 << 6,
}

public static class TranslateServiceTypeExtensions
{
    public static TranslateServiceType LLMServices =>
        TranslateServiceType.Ollama |
        TranslateServiceType.LMStudio |
        TranslateServiceType.OpenAI |
        TranslateServiceType.Claude;

    public static bool IsLLM(this TranslateServiceType serviceType)
    {
        return LLMServices.HasFlag(serviceType);
    }

    public static ITranslateSettings DefaultSettings(this TranslateServiceType serviceType)
    {
        switch (serviceType)
        {
            case TranslateServiceType.GoogleV1:
                return new GoogleV1TranslateSettings();
            case TranslateServiceType.DeepL:
                return new DeepLTranslateSettings();
            case TranslateServiceType.DeepLX:
                return new DeepLXTranslateSettings();
            case TranslateServiceType.Ollama:
                return new OllamaTranslateSettings();
            case TranslateServiceType.LMStudio:
                return new LMStudioTranslateSettings();
            case TranslateServiceType.OpenAI:
                return new OpenAITranslateSettings();
            case TranslateServiceType.Claude:
                return new ClaudeTranslateSettings();
        }

        throw new InvalidOperationException();
    }
}

public static class TranslateServiceHelper
{
    /// <summary>
    /// TryGetLanguage
    /// </summary>
    /// <param name="service"></param>
    /// <param name="src"></param>
    /// <param name="target"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    public static (TranslateLanguage srcLang, TranslateLanguage targetLang) TryGetLanguage(this ITranslateService service, Language src, TargetLanguage target)
    {
        string iso6391 = src.ISO6391;

        // TODO: L: Allow the user to choose auto-detection by translation provider?
        if (src == Language.Unknown)
        {
            throw new TranslationConfigException("source language are unknown");
        }

        if (src.ISO6391 == target.ToISO6391())
        {
            // Only chinese allow translation between regions (Simplified <-> Traditional)
            // Portuguese, French, and English are not permitted.
            // TODO: L: review this validation?
            if (target is not (TargetLanguage.ChineseSimplified or TargetLanguage.ChineseTraditional))
            {
                throw new TranslationConfigException("source and target language are same");
            }
        }

        if (!TranslateLanguage.Langs.TryGetValue(iso6391, out TranslateLanguage srcLang))
        {
            throw new TranslationConfigException($"source language is not supported: {src.TopEnglishName}");
        }

        if (!srcLang.SupportedServices.HasFlag(service.ServiceType))
        {
            throw new TranslationConfigException($"source language is not supported by {service.ServiceType}: {src.TopEnglishName}");
        }

        if (!TranslateLanguage.Langs.TryGetValue(target.ToISO6391(), out TranslateLanguage targetLang))
        {
            throw new TranslationConfigException($"target language is not supported: {target.ToString()}");
        }

        if (!targetLang.SupportedServices.HasFlag(service.ServiceType))
        {
            throw new TranslationConfigException($"target language is not supported by {service.ServiceType}: {src.TopEnglishName}");
        }

        return (srcLang, targetLang);
    }
}

public class TranslationException : Exception
{
    public TranslationException()
    {
    }

    public TranslationException(string message)
        : base(message)
    {
    }

    public TranslationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class TranslationConfigException : Exception
{
    public TranslationConfigException()
    {
    }

    public TranslationConfigException(string message)
        : base(message)
    {
    }

    public TranslationConfigException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
