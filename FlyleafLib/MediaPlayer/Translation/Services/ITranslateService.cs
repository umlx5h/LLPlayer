using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

public interface ITranslateService
{
    /// <summary>
    /// Initialize
    /// </summary>
    /// <param name="src"></param>
    /// <param name="target"></param>
    /// <exception cref="ArgumentException">when language is not supported</exception>
    void Initialize(Language src, TargetLanguage target);

    /// <summary>
    /// TranslateAsync
    /// </summary>
    /// <param name="text"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TranslationException">when translation is failed</exception>
    Task<string> TranslateAsync(string text, CancellationToken token);
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
    DeepLX = 1 << 2
}

public static class TranslateServiceTypeExtensions
{
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
        }

        throw new InvalidOperationException();
    }
}
