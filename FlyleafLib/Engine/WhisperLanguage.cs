using System.Collections.Generic;
using System.Linq;

namespace FlyleafLib;

public class WhisperLanguage
{
    public string Code { get; set; }
    public string EnglishName { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not WhisperLanguage lang)
            return false;

        return lang.Code == Code;
    }

    // can get by calling WhisperFactory.GetSupportedLanguages()
    // calling the above method will load the whisper runtime implicitly,
    // As a result, unintended runtime may be used, so temporarily manually manage language codes
    // ref: https://github.com/ggerganov/whisper.cpp/blob/d682e150908e10caa4c15883c633d7902d385237/src/whisper.cpp#L248-L348
    private static List<string> WhisperLanguageCodes =
    [
        "en",
        "zh",
        "de",
        "es",
        "ru",
        "ko",
        "fr",
        "ja",
        "pt",
        "tr",
        "pl",
        "ca",
        "nl",
        "ar",
        "sv",
        "it",
        "id",
        "hi",
        "fi",
        "vi",
        "he",
        "uk",
        "el",
        "ms",
        "cs",
        "ro",
        "da",
        "hu",
        "ta",
        "no",
        "th",
        "ur",
        "hr",
        "bg",
        "lt",
        "la",
        "mi",
        "ml",
        "cy",
        "sk",
        "te",
        "fa",
        "lv",
        "bn",
        "sr",
        "az",
        "sl",
        "kn",
        "et",
        "mk",
        "br",
        "eu",
        "is",
        "hy",
        "ne",
        "mn",
        "bs",
        "kk",
        "sq",
        "sw",
        "gl",
        "mr",
        "pa",
        "si",
        "km",
        "sn",
        "yo",
        "so",
        "af",
        "oc",
        "ka",
        "be",
        "tg",
        "sd",
        "gu",
        "am",
        "yi",
        "lo",
        "uz",
        "fo",
        "ht",
        "ps",
        "tk",
        "nn",
        "mt",
        "sa",
        "lb",
        "my",
        "bo",
        "tl",
        "mg",
        "as",
        "tt",
        "haw",
        "ln",
        "ha",
        "ba",
        "jw",
        "su"
    ];

    public static List<WhisperLanguage> GetWhisperLanguages()
    {
        //List<WhisperLanguage> whisperLangs = WhisperFactory.GetSupportedLanguages()
        List<WhisperLanguage> whisperLangs = WhisperLanguageCodes
            .Select(code =>
            {
                var lang = Language.Get(code);

                string englishName = lang == Language.Unknown ? code : lang.TopEnglishName;

                return new WhisperLanguage { Code = code, EnglishName = englishName };
            })
            .OrderBy(l => l.EnglishName)
            .ToList();

        return whisperLangs;
    }
}
