using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FlyleafLib;

#nullable enable

// Whisper Common Config (whisper.cpp, faster-whisper)
public class WhisperConfig : NotifyPropertyChanged
{
    public static string ModelsDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whispermodels");
    public static string EnginesDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Whisper");

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

    public string Language
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(LanguageName);
            }
        }
    } = "en";

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
    } = true;

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

    public static void EnsureModelsDirectory()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            Directory.CreateDirectory(ModelsDirectory);
        }
    }

    public static void EnsureEnginesDirectory()
    {
        if (!Directory.Exists(EnginesDirectory))
        {
            Directory.CreateDirectory(EnginesDirectory);
        }
    }
}

// TODO: L: Add other options
public class WhisperCppConfig : NotifyPropertyChanged
{
    public WhisperCppModel? Model
    {
        get;
        // When binding in the configuration GUI, the check is set to false to update the current size.
        set => Set(ref field, value, false);
    }

    public List<RuntimeLibrary> RuntimeLibraries { get; set => Set(ref field, value); } = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
    public RuntimeLibrary? LoadedLibrary => RuntimeOptions.LoadedLibrary;

    public int? GpuDevice { get; set => Set(ref field, value); }

    public int? Threads { get; set => Set(ref field, value); }
    public int? MaxSegmentLength { get; set => Set(ref field, value); }
    public int? MaxTokensPerSegment { get; set => Set(ref field, value); }
    public bool SplitOnWord { get; set => Set(ref field, value); }
    public float? NoSpeechThreshold { get; set => Set(ref field, value); }
    public bool NoContext { get; set => Set(ref field, value); }
    public int? AudioContextSize { get; set => Set(ref field, value); }
    public string Prompt { get; set => Set(ref field, value); } = string.Empty;

    public WhisperFactoryOptions GetFactoryOptions()
    {
        WhisperFactoryOptions opts = WhisperFactoryOptions.Default;

        if (GpuDevice.HasValue)
            opts.GpuDevice = GpuDevice.Value;

        return opts;
    }

    public WhisperProcessorBuilder ConfigureBuilder(WhisperConfig whisperConfig, WhisperProcessorBuilder builder)
    {
        if (!string.IsNullOrEmpty(whisperConfig.Language))
            builder.WithLanguage(whisperConfig.Language);

        // prefer auto
        if (whisperConfig.LanguageDetection)
            builder.WithLanguageDetection();

        if (whisperConfig.Translate)
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

        if (!string.IsNullOrWhiteSpace(Prompt))
            builder.WithPrompt(Prompt);

        // auto set
        if (MaxSegmentLength is > 0 || MaxSegmentLength is > 0)
            builder.WithTokenTimestamps();

        return builder;
    }
}

public class FasterWhisperConfig : NotifyPropertyChanged
{
    public static string DefaultEnginePath { get; } = Path.Combine(WhisperConfig.EnginesDirectory, "Faster-Whisper-XXL", "faster-whisper-xxl.exe");

    // can get by faster-whisper-xxl.exe --model foo bar.wav
    public static List<string> ModelOptions { get; } = [
        "tiny",
        "tiny.en",
        "base",
        "base.en",
        "small",
        "small.en",
        "medium",
        "medium.en",
        "large-v1",
        "large-v2",
        "large-v3",
        //"large", // = large-v3
        "large-v3-turbo",
        //"turbo", // = large-v3-turbo
        "distil-large-v2",
        "distil-medium.en",
        "distil-small.en",
        "distil-large-v3",
        "distil-large-v3.5"
    ];

    public bool UseManualEngine { get; set => Set(ref field, value); }
    public string? ManualEnginePath { get; set => Set(ref field, value); }
    public bool UseManualModel { get; set => Set(ref field, value); }
    public string? ManualModelDir { get; set => Set(ref field, value); }
    public string Model { get; set => Set(ref field, value); } = "tiny";
    public string ExtraArguments { get; set => Set(ref field, value); } = string.Empty;

    public ProcessPriorityClass ProcessPriority { get; set => Set(ref field, value); } = ProcessPriorityClass.Normal;
}
