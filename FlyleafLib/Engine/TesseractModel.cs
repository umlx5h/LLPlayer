using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlyleafLib;

public class TesseractModel : NotifyPropertyChanged
{
    public static Dictionary<TesseractOCR.Enums.Language, string> TesseractLangToISO6391 = new()
    {
        {TesseractOCR.Enums.Language.Afrikaans, "af"},
        {TesseractOCR.Enums.Language.Amharic, "am"},
        {TesseractOCR.Enums.Language.Arabic, "ar"},
        {TesseractOCR.Enums.Language.Assamese, "as"},
        {TesseractOCR.Enums.Language.Azerbaijani, "az"},
        //{TesseractOCR.Enums.Language.AzerbaijaniCyrilic, ""},
        {TesseractOCR.Enums.Language.Belarusian, "be"},
        {TesseractOCR.Enums.Language.Bengali, "bn"},
        {TesseractOCR.Enums.Language.Tibetan, "bo"},
        {TesseractOCR.Enums.Language.Bosnian, "bs"},
        {TesseractOCR.Enums.Language.Breton, "br"},
        {TesseractOCR.Enums.Language.Bulgarian, "bg"},
        {TesseractOCR.Enums.Language.CatalanValencian, "ca"},
        //{TesseractOCR.Enums.Language.Cebuano, ""},
        {TesseractOCR.Enums.Language.Czech, "cs"},
        {TesseractOCR.Enums.Language.ChineseSimplified, "zh"}, // do special handling
        {TesseractOCR.Enums.Language.ChineseTraditional, "zh"},
        //{TesseractOCR.Enums.Language.Cherokee, ""},
        {TesseractOCR.Enums.Language.Corsican, "co"},
        {TesseractOCR.Enums.Language.Welsh, "cy"},
        {TesseractOCR.Enums.Language.Danish, "da"},
        //{TesseractOCR.Enums.Language.DanishFraktur, ""},
        {TesseractOCR.Enums.Language.German, "de"},
        //{TesseractOCR.Enums.Language.GermanFrakturContrib, ""},
        {TesseractOCR.Enums.Language.Dzongkha, "dz"},
        {TesseractOCR.Enums.Language.GreekModern, "el"},
        {TesseractOCR.Enums.Language.English, "en"},
        //{TesseractOCR.Enums.Language.EnglishMiddle, ""},
        {TesseractOCR.Enums.Language.Esperanto, "eo"},
        //{TesseractOCR.Enums.Language.Math, "zz"},
        {TesseractOCR.Enums.Language.Estonian, "et"},
        {TesseractOCR.Enums.Language.Basque, "eu"},
        {TesseractOCR.Enums.Language.Faroese, "fo"},
        {TesseractOCR.Enums.Language.Persian, "fa"},
        //{TesseractOCR.Enums.Language.Filipino, ""},
        {TesseractOCR.Enums.Language.Finnish, "fi"},
        {TesseractOCR.Enums.Language.French, "fr"},
        //{TesseractOCR.Enums.Language.GermanFraktur, ""},
        {TesseractOCR.Enums.Language.FrenchMiddle, "zz"},
        //{TesseractOCR.Enums.Language.WesternFrisian, ""},
        {TesseractOCR.Enums.Language.ScottishGaelic, "gd"},
        {TesseractOCR.Enums.Language.Irish, "ga"},
        {TesseractOCR.Enums.Language.Galician, "gl"},
        //{TesseractOCR.Enums.Language.GreekAncientContrib, ""},
        {TesseractOCR.Enums.Language.Gujarati, "gu"},
        {TesseractOCR.Enums.Language.Haitian, "ht"},
        {TesseractOCR.Enums.Language.Hebrew, "he"},
        {TesseractOCR.Enums.Language.Hindi, "hi"},
        {TesseractOCR.Enums.Language.Croatian, "hr"},
        {TesseractOCR.Enums.Language.Hungarian, "hu"},
        {TesseractOCR.Enums.Language.Armenian, "hy"},
        {TesseractOCR.Enums.Language.Inuktitut, "iu"},
        {TesseractOCR.Enums.Language.Indonesian, "id"},
        {TesseractOCR.Enums.Language.Icelandic, "is"},
        {TesseractOCR.Enums.Language.Italian, "it"},
        //{TesseractOCR.Enums.Language.ItalianOld, ""},
        {TesseractOCR.Enums.Language.Javanese, "jv"},
        {TesseractOCR.Enums.Language.Japanese, "ja"},
        //{TesseractOCR.Enums.Language.JapaneseVertical, ""},
        {TesseractOCR.Enums.Language.Kannada, "kn"},
        {TesseractOCR.Enums.Language.Georgian, "ka"},
        //{TesseractOCR.Enums.Language.GeorgianOld, ""},
        {TesseractOCR.Enums.Language.Kazakh, "kk"},
        {TesseractOCR.Enums.Language.CentralKhmer, "km"},
        {TesseractOCR.Enums.Language.KirghizKyrgyz, "ky"},
        {TesseractOCR.Enums.Language.Kurmanji, "ku"},
        {TesseractOCR.Enums.Language.Korean, "ko"},
        //{TesseractOCR.Enums.Language.KoreanVertical, ""},
        //{TesseractOCR.Enums.Language.KurdishArabicScript, ""},
        {TesseractOCR.Enums.Language.Lao, "lo"},
        {TesseractOCR.Enums.Language.Latin, "la"},
        {TesseractOCR.Enums.Language.Latvian, "lv"},
        {TesseractOCR.Enums.Language.Lithuanian, "lt"},
        {TesseractOCR.Enums.Language.Luxembourgish, "lb"},
        {TesseractOCR.Enums.Language.Malayalam, "ml"},
        {TesseractOCR.Enums.Language.Marathi, "mr"},
        {TesseractOCR.Enums.Language.Macedonian, "mk"},
        {TesseractOCR.Enums.Language.Maltese, "mt"},
        {TesseractOCR.Enums.Language.Mongolian, "mn"},
        {TesseractOCR.Enums.Language.Maori, "mi"},
        {TesseractOCR.Enums.Language.Malay, "ms"},
        {TesseractOCR.Enums.Language.Burmese, "my"},
        {TesseractOCR.Enums.Language.Nepali, "ne"},
        {TesseractOCR.Enums.Language.Dutch, "nl"},
        {TesseractOCR.Enums.Language.Norwegian, "no"},
        {TesseractOCR.Enums.Language.Occitan, "oc"},
        {TesseractOCR.Enums.Language.Oriya, "or"},
        //{TesseractOCR.Enums.Language.Osd, ""},
        {TesseractOCR.Enums.Language.Panjabi, "pa"},
        {TesseractOCR.Enums.Language.Polish, "pl"},
        {TesseractOCR.Enums.Language.Portuguese, "pt"},
        {TesseractOCR.Enums.Language.Pushto, "ps"},
        {TesseractOCR.Enums.Language.Quechua, "qu"},
        {TesseractOCR.Enums.Language.Romanian, "ro"},
        {TesseractOCR.Enums.Language.Russian, "ru"},
        {TesseractOCR.Enums.Language.Sanskrit, "sa"},
        {TesseractOCR.Enums.Language.Sinhala, "si"},
        {TesseractOCR.Enums.Language.Slovak, "sk"},
        //{TesseractOCR.Enums.Language.SlovakFrakturContrib, ""},
        {TesseractOCR.Enums.Language.Slovenian, "sl"},
        {TesseractOCR.Enums.Language.Sindhi, "sd"},
        {TesseractOCR.Enums.Language.SpanishCastilian, "es"},
        //{TesseractOCR.Enums.Language.SpanishCastilianOld, ""},
        {TesseractOCR.Enums.Language.Albanian, "sq"},
        {TesseractOCR.Enums.Language.Serbian, "sr"},
        //{TesseractOCR.Enums.Language.SerbianLatin, ""},
        {TesseractOCR.Enums.Language.Sundanese, "su"},
        {TesseractOCR.Enums.Language.Swahili, "sw"},
        {TesseractOCR.Enums.Language.Swedish, "sv"},
        //{TesseractOCR.Enums.Language.Syriac, ""},
        {TesseractOCR.Enums.Language.Tamil, "ta"},
        {TesseractOCR.Enums.Language.Tatar, "tt"},
        {TesseractOCR.Enums.Language.Telugu, "te"},
        {TesseractOCR.Enums.Language.Tajik, "tg"},
        {TesseractOCR.Enums.Language.Tagalog, "tl"},
        {TesseractOCR.Enums.Language.Thai, "th"},
        {TesseractOCR.Enums.Language.Tigrinya, "ti"},
        {TesseractOCR.Enums.Language.Tonga, "to"},
        {TesseractOCR.Enums.Language.Turkish, "tr"},
        {TesseractOCR.Enums.Language.Uighur, "ug"},
        {TesseractOCR.Enums.Language.Ukrainian, "uk"},
        {TesseractOCR.Enums.Language.Urdu, "ur"},
        {TesseractOCR.Enums.Language.Uzbek, "uz"},
        //{TesseractOCR.Enums.Language.UzbekCyrilic, ""},
        {TesseractOCR.Enums.Language.Vietnamese, "vi"},
        {TesseractOCR.Enums.Language.Yiddish, "yi"},
        {TesseractOCR.Enums.Language.Yoruba, "yo"},
    };

    public static string ModelsDirectory { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tesseractmodels", "tessdata");

    public TesseractOCR.Enums.Language Lang { get; set; }

    public string ISO6391 => TesseractLangToISO6391[Lang];

    public string LangCode =>
        TesseractOCR.Enums.LanguageHelper.EnumToString(Lang);

    public long Size
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Downloaded));
            }
        }
    }

    public string ModelFileName => $"{LangCode}.traineddata";

    public string ModelFilePath => Path.Combine(ModelsDirectory, ModelFileName);

    public bool Downloaded => Size > 0;

    public override string ToString() => LangCode;

    public override bool Equals(object? obj)
    {
        if (obj is not TesseractModel model)
            return false;

        return model.Lang == Lang;
    }

    public override int GetHashCode() => (int)Lang;
}

public class TesseractModelLoader
{
    /// <summary>
    /// key: ISO6391, value: TesseractModel
    /// Chinese has multiple models for one ISO6391 key
    /// </summary>
    /// <returns></returns>
    internal static Dictionary<string, List<TesseractModel>> GetAvailableModels()
    {
        List<TesseractModel> models = LoadDownloadedModels();

        Dictionary<string, List<TesseractModel>> dict = new();

        foreach (TesseractModel model in models)
        {
            if (TesseractModel.TesseractLangToISO6391.TryGetValue(model.Lang, out string iso6391))
            {
                if (dict.ContainsKey(iso6391))
                {
                    // for chinese (zh-CN, zh-TW)
                    dict[iso6391].Add(model);
                }
                else
                {
                    dict.Add(iso6391, [model]);
                }
            }
        }

        return dict;
    }

    public static List<TesseractModel> LoadAllModels()
    {
        EnsureModelsDirectory();

        List<TesseractModel> models = Enum.GetValues<TesseractOCR.Enums.Language>()
            .Where(l => TesseractModel.TesseractLangToISO6391.ContainsKey(l))
            .Select(l => new TesseractModel { Lang = l })
            .OrderBy(m => m.Lang.ToString())
            .ToList();

        foreach (TesseractModel model in models)
        {
            // Initialize download status of each model
            string path = model.ModelFilePath;
            if (File.Exists(path))
            {
                model.Size = new FileInfo(path).Length;
            }
        }

        return models;
    }

    public static List<TesseractModel> LoadDownloadedModels()
    {
        return LoadAllModels()
            .Where(m => m.Downloaded)
            .ToList();
    }

    private static void EnsureModelsDirectory()
    {
        if (!Directory.Exists(TesseractModel.ModelsDirectory))
        {
            Directory.CreateDirectory(TesseractModel.ModelsDirectory);
        }
    }
}
