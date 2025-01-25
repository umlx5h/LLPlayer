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
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public ITranslateService GetService(TranslateServiceType serviceType)
    {
        switch (serviceType)
        {
            case TranslateServiceType.GoogleV1:
                if (!_config.TranslateServiceSettings.TryGetValue(serviceType, out var g))
                {
                    // Default settings
                    g = new GoogleV1TranslateSettings();
                }

                return new GoogleV1TranslateService((GoogleV1TranslateSettings)g);

            case TranslateServiceType.DeepL:
                if (!_config.TranslateServiceSettings.TryGetValue(serviceType, out var d))
                {
                    throw new TranslationConfigException("DeepL Service settings is not configured");
                }

                return new DeepLTranslateService((DeepLTranslateSettings)d);

            case TranslateServiceType.DeepLX:
                if (!_config.TranslateServiceSettings.TryGetValue(serviceType, out var dx))
                {
                    throw new TranslationConfigException("DeepLX Service settings is not configured");
                }

                return new DeepLXTranslateService((DeepLXTranslateSettings)dx);
        }

        throw new InvalidOperationException($"Translate service {serviceType} does not exist.");
    }
}
