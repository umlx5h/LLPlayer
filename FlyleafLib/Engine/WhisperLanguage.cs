using System.Collections.Generic;
using System.Linq;
using System.Text;

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

    // https://github.com/ggerganov/whisper.cpp/blob/d682e150908e10caa4c15883c633d7902d385237/src/whisper.cpp#L248-L348
    private static readonly Dictionary<string, string> CodeToLanguage = new()
    {
        {"en", "english"},
        {"zh", "chinese"},
        {"de", "german"},
        {"es", "spanish"},
        {"ru", "russian"},
        {"ko", "korean"},
        {"fr", "french"},
        {"ja", "japanese"},
        {"pt", "portuguese"},
        {"tr", "turkish"},
        {"pl", "polish"},
        {"ca", "catalan"},
        {"nl", "dutch"},
        {"ar", "arabic"},
        {"sv", "swedish"},
        {"it", "italian"},
        {"id", "indonesian"},
        {"hi", "hindi"},
        {"fi", "finnish"},
        {"vi", "vietnamese"},
        {"he", "hebrew"},
        {"uk", "ukrainian"},
        {"el", "greek"},
        {"ms", "malay"},
        {"cs", "czech"},
        {"ro", "romanian"},
        {"da", "danish"},
        {"hu", "hungarian"},
        {"ta", "tamil"},
        {"no", "norwegian"},
        {"th", "thai"},
        {"ur", "urdu"},
        {"hr", "croatian"},
        {"bg", "bulgarian"},
        {"lt", "lithuanian"},
        {"la", "latin"},
        {"mi", "maori"},
        {"ml", "malayalam"},
        {"cy", "welsh"},
        {"sk", "slovak"},
        {"te", "telugu"},
        {"fa", "persian"},
        {"lv", "latvian"},
        {"bn", "bengali"},
        {"sr", "serbian"},
        {"az", "azerbaijani"},
        {"sl", "slovenian"},
        {"kn", "kannada"},
        {"et", "estonian"},
        {"mk", "macedonian"},
        {"br", "breton"},
        {"eu", "basque"},
        {"is", "icelandic"},
        {"hy", "armenian"},
        {"ne", "nepali"},
        {"mn", "mongolian"},
        {"bs", "bosnian"},
        {"kk", "kazakh"},
        {"sq", "albanian"},
        {"sw", "swahili"},
        {"gl", "galician"},
        {"mr", "marathi"},
        {"pa", "punjabi"},
        {"si", "sinhala"},
        {"km", "khmer"},
        {"sn", "shona"},
        {"yo", "yoruba"},
        {"so", "somali"},
        {"af", "afrikaans"},
        {"oc", "occitan"},
        {"ka", "georgian"},
        {"be", "belarusian"},
        {"tg", "tajik"},
        {"sd", "sindhi"},
        {"gu", "gujarati"},
        {"am", "amharic"},
        {"yi", "yiddish"},
        {"lo", "lao"},
        {"uz", "uzbek"},
        {"fo", "faroese"},
        {"ht", "haitian creole"},
        {"ps", "pashto"},
        {"tk", "turkmen"},
        {"nn", "nynorsk"},
        {"mt", "maltese"},
        {"sa", "sanskrit"},
        {"lb", "luxembourgish"},
        {"my", "myanmar"},
        {"bo", "tibetan"},
        {"tl", "tagalog"},
        {"mg", "malagasy"},
        {"as", "assamese"},
        {"tt", "tatar"},
        {"haw", "hawaiian"},
        {"ln", "lingala"},
        {"ha", "hausa"},
        {"ba", "bashkir"},
        {"jw", "javanese"},
        {"su", "sundanese"},
        {"yue", "cantonese"},
    };

    // https://github.com/Purfview/whisper-standalone-win/issues/430#issuecomment-2743029023
    // https://github.com/openai/whisper/blob/517a43ecd132a2089d85f4ebc044728a71d49f6e/whisper/tokenizer.py#L10-L110
    public static Dictionary<string, string> LanguageToCode { get; } = CodeToLanguage.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static List<WhisperLanguage> GetWhisperLanguages()
    {
        return CodeToLanguage.Select(kv => new WhisperLanguage
        {
            Code = kv.Key,
            EnglishName = UpperFirstOfWords(kv.Value)
        }).OrderBy(l => l.EnglishName).ToList();
    }

    private static string UpperFirstOfWords(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new();
        bool isNewWord = true;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                isNewWord = true;
                result.Append(c);
            }
            else if (isNewWord)
            {
                result.Append(char.ToUpper(c));
                isNewWord = false;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
