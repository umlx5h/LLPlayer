using System.Collections.Generic;
using System.Linq;
using Whisper.net;

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

    public static List<WhisperLanguage> GetWhisperLanguages()
    {
        List<WhisperLanguage> whisperLangs = WhisperFactory.GetSupportedLanguages()
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
