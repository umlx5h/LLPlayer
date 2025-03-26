using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FlyleafLib;

#nullable enable
// TODO: L: Add other options
public class WhisperParameters : NotifyPropertyChanged
{
    public RuntimeLibrary? LoadedLibrary => RuntimeOptions.LoadedLibrary;

    // For UI
    public string LanguageName
    {
        get
        {
            string translate = Translate ? ", Trans" : "";

            if (LanguageDetection)
            {
                return "Auto" + translate;
            }

            var lang = FlyleafLib.Language.Get(Language);

            if (lang != null)
            {
                return lang.TopEnglishName + translate;
            }

            return Language + translate;
        }
    }

    public string? Language
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(LanguageName);
            }
        }
    }

    public bool LanguageDetection
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(LanguageName);
            }
        }
    }

    public bool Translate
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(LanguageName);
            }
        }
    }

    public int? Threads { get; set => Set(ref field, value); }

    public int? MaxSegmentLength { get; set => Set(ref field, value); }

    public int? MaxTokensPerSegment { get; set => Set(ref field, value); }

    public bool SplitOnWord { get; set => Set(ref field, value); }

    public float? NoSpeechThreshold { get; set => Set(ref field, value); }

    public bool NoContext { get; set => Set(ref field, value); }

    public int? AudioContextSize { get; set => Set(ref field, value); }

    public WhisperProcessorBuilder ConfigureBuilder(WhisperProcessorBuilder builder)
    {
        if (!string.IsNullOrEmpty(Language))
            builder.WithLanguage(Language);

        // prefer auto
        if (LanguageDetection)
            builder.WithLanguageDetection();

        if (Translate)
            builder.WithTranslate();

        if (Threads is > 0)
            builder.WithThreads(Threads.Value);

        if (MaxSegmentLength is > 0)
            builder.WithMaxSegmentLength(MaxSegmentLength.Value);

        if (MaxTokensPerSegment is > 0)
            builder.WithMaxTokensPerSegment(MaxTokensPerSegment.Value);

        if (SplitOnWord)
            builder.SplitOnWord();

        if (NoSpeechThreshold is > 0)
            builder.WithNoSpeechThreshold(NoSpeechThreshold.Value);

        if (NoContext)
            builder.WithNoContext();

        if (AudioContextSize is > 0)
            builder.WithAudioContextSize(AudioContextSize.Value);

        // auto set
        if (MaxSegmentLength is > 0 || MaxSegmentLength is > 0)
            builder.WithTokenTimestamps();

        return builder;
    }

    public static WhisperParameters DefaultParameters()
    {
        WhisperParameters p = new()
        {
            LanguageDetection = true,
        };

        return p;
    }
}
