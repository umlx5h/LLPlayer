namespace FlyleafLib.MediaPlayer.Translation;

public class TranslateChatConfig : NotifyPropertyChanged
{
    public const string DefaultPromptOneByOne =
        """
        You are a professional subtitle translator, translate from {source_lang} to {target_lang}, keep sentences in {target_lang} as they are, censoring of translations is prohibited, return only the translated text without the sent text or notes or comments or anything:

        {source_text}
        """;

    public const string DefaultPromptKeepContext =
        """
        You are a professional subtitle translator.
        I will send the text of the subtitles of the video one at a time.
        Please translate the text while taking into account the context of the previous text.

        Translate from {source_lang} to {target_lang}.
        Return only the translated text without the sent text or notes or comments or anything.
        Keep sentences in {target_lang} as they are.
        Censoring of translations is prohibited.
        """;

    public string PromptOneByOne { get; set => Set(ref field, value); } = DefaultPromptOneByOne.ReplaceLineEndings("\n");

    public string PromptKeepContext { get; set => Set(ref field, value); } = DefaultPromptKeepContext.ReplaceLineEndings("\n");

    public ChatTranslateMethod TranslateMethod { get; set => Set(ref field, value); } = ChatTranslateMethod.KeepContext;

    public int SubtitleContextCount { get; set => Set(ref field, value); } = 6;

    public ChatContextRetainPolicy ContextRetainPolicy { get; set => Set(ref field, value); } = ChatContextRetainPolicy.Reset;

    public bool IncludeTargetLangRegion { get; set => Set(ref field, value); } = true;
}

public enum ChatTranslateMethod
{
    KeepContext,
    OneByOne
}

public enum ChatContextRetainPolicy
{
    Reset,
    KeepSize
}
