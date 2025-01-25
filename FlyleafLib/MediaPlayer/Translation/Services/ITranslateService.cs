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
