using System.Collections.Generic;

namespace FlyleafLib.MediaPlayer.Translation.Services;

public class TranslateServiceFactory
{
    private readonly Config.SubtitlesConfig _config;

    public TranslateServiceFactory(Config.SubtitlesConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// GetService
    /// </summary>
    /// <param name="serviceType"></param>
    /// <param name="wordMode"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public ITranslateService GetService(TranslateServiceType serviceType, bool wordMode)
    {
        switch (serviceType)
        {
            case TranslateServiceType.GoogleV1:
                return new GoogleV1TranslateService((GoogleV1TranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new GoogleV1TranslateSettings()));

            case TranslateServiceType.DeepL:
                return new DeepLTranslateService((DeepLTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new DeepLTranslateSettings()));

            case TranslateServiceType.DeepLX:
                return new DeepLXTranslateService((DeepLXTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new DeepLXTranslateSettings()));

            case TranslateServiceType.Ollama:
                return new OpenAIBaseTranslateService((OllamaTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new OllamaTranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.LMStudio:
                return new OpenAIBaseTranslateService((LMStudioTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new LMStudioTranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.KoboldCpp:
                return new OpenAIBaseTranslateService((KoboldCppTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new KoboldCppTranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.OpenAI:
                return new OpenAIBaseTranslateService((OpenAITranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new OpenAITranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.OpenAILike:
                return new OpenAIBaseTranslateService((OpenAILikeTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new OpenAILikeTranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.Claude:
                return new OpenAIBaseTranslateService((ClaudeTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new ClaudeTranslateSettings()), _config.TranslateChatConfig, wordMode);

            case TranslateServiceType.LiteLLM:
                return new OpenAIBaseTranslateService((LiteLLMTranslateSettings)_config.TranslateServiceSettings.GetValueOrDefault(serviceType, new LiteLLMTranslateSettings()), _config.TranslateChatConfig, wordMode);
        }

        throw new InvalidOperationException($"Translate service {serviceType} does not exist.");
    }
}
