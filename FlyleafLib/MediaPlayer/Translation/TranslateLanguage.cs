using FlyleafLib.MediaPlayer.Translation.Services;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace FlyleafLib.MediaPlayer.Translation;

public class TranslateLanguage
{
    public static Dictionary<string, TranslateLanguage> Langs = new()
    {
        // Google: https://cloud.google.com/translate/docs/languages
        // DeepL: https://developers.deepl.com/docs/resources/supported-languages
        ["ab"] = new("Abkhaz", "ab", TranslateServiceType.GoogleV1),
        ["af"] = new("Afrikaans", "af", TranslateServiceType.GoogleV1),
        ["sq"] = new("Albanian", "sq", TranslateServiceType.GoogleV1),
        ["am"] = new("Amharic", "am", TranslateServiceType.GoogleV1),
        ["ar"] = new("Arabic", "ar"),
        ["hy"] = new("Armenian", "hy", TranslateServiceType.GoogleV1),
        ["as"] = new("Assamese", "as", TranslateServiceType.GoogleV1),
        ["ay"] = new("Aymara", "ay", TranslateServiceType.GoogleV1),
        ["az"] = new("Azerbaijani", "az", TranslateServiceType.GoogleV1),
        ["bm"] = new("Bambara", "bm", TranslateServiceType.GoogleV1),
        ["ba"] = new("Bashkir", "ba", TranslateServiceType.GoogleV1),
        ["eu"] = new("Basque", "eu", TranslateServiceType.GoogleV1),
        ["be"] = new("Belarusian", "be", TranslateServiceType.GoogleV1),
        ["bn"] = new("Bengali", "bn", TranslateServiceType.GoogleV1),
        ["bs"] = new("Bosnian", "bs", TranslateServiceType.GoogleV1),
        ["br"] = new("Breton", "br", TranslateServiceType.GoogleV1),
        ["bg"] = new("Bulgarian", "bg"),
        ["ca"] = new("Catalan", "ca", TranslateServiceType.GoogleV1),
        ["ny"] = new("Chichewa (Nyanja)", "ny", TranslateServiceType.GoogleV1),

        // special handling
        ["zh"] = new("Chinese", "zh"),

        ["cv"] = new("Chuvash", "cv", TranslateServiceType.GoogleV1),
        ["co"] = new("Corsican", "co", TranslateServiceType.GoogleV1),
        ["hr"] = new("Croatian", "hr", TranslateServiceType.GoogleV1),
        ["cs"] = new("Czech", "cs"),
        ["da"] = new("Danish", "da"),
        ["dv"] = new("Divehi", "dv", TranslateServiceType.GoogleV1),
        ["nl"] = new("Dutch", "nl"),
        ["dz"] = new("Dzongkha", "dz", TranslateServiceType.GoogleV1),
        ["en"] = new("English", "en"),
        ["eo"] = new("Esperanto", "eo", TranslateServiceType.GoogleV1),
        ["et"] = new("Estonian", "et"),
        ["ee"] = new("Ewe", "ee", TranslateServiceType.GoogleV1),
        ["fj"] = new("Fijian", "fj", TranslateServiceType.GoogleV1),
        ["tl"] = new("Tagalog", "tl", TranslateServiceType.GoogleV1),
        ["fi"] = new("Finnish", "fi"),

        // special handling
        ["fr"] = new("French", "fr"),

        ["fy"] = new("Frisian", "fy", TranslateServiceType.GoogleV1),
        ["ff"] = new("Fulfulde", "ff", TranslateServiceType.GoogleV1),
        ["gl"] = new("Galician", "gl", TranslateServiceType.GoogleV1),
        ["lg"] = new("Ganda (Luganda)", "lg", TranslateServiceType.GoogleV1),
        ["ka"] = new("Georgian", "ka", TranslateServiceType.GoogleV1),
        ["de"] = new("German", "de"),
        ["el"] = new("Greek", "el"),
        ["gn"] = new("Guarani", "gn", TranslateServiceType.GoogleV1),
        ["gu"] = new("Gujarati", "gu", TranslateServiceType.GoogleV1),
        ["ht"] = new("Haitian Creole", "ht", TranslateServiceType.GoogleV1),
        ["ha"] = new("Hausa", "ha", TranslateServiceType.GoogleV1),
        ["he"] = new("Hebrew", "he", TranslateServiceType.GoogleV1),
        ["hi"] = new("Hindi", "hi", TranslateServiceType.GoogleV1),
        ["hu"] = new("Hungarian", "hu"),
        ["is"] = new("Icelandic", "is", TranslateServiceType.GoogleV1),
        ["ig"] = new("Igbo", "ig", TranslateServiceType.GoogleV1),
        ["id"] = new("Indonesian", "id"),
        ["ga"] = new("Irish", "ga", TranslateServiceType.GoogleV1),
        ["it"] = new("Italian", "it"),
        ["ja"] = new("Japanese", "ja"),
        ["jv"] = new("Javanese", "jv", TranslateServiceType.GoogleV1),
        ["kn"] = new("Kannada", "kn", TranslateServiceType.GoogleV1),
        ["kk"] = new("Kazakh", "kk", TranslateServiceType.GoogleV1),
        ["km"] = new("Khmer", "km", TranslateServiceType.GoogleV1),
        ["rw"] = new("Kinyarwanda", "rw", TranslateServiceType.GoogleV1),
        ["ko"] = new("Korean", "ko"),
        ["ku"] = new("Kurdish (Kurmanji)", "ku", TranslateServiceType.GoogleV1),
        ["ky"] = new("Kyrgyz", "ky", TranslateServiceType.GoogleV1),
        ["lo"] = new("Lao", "lo", TranslateServiceType.GoogleV1),
        ["la"] = new("Latin", "la", TranslateServiceType.GoogleV1),
        ["lv"] = new("Latvian", "lv"),
        ["li"] = new("Limburgan", "li", TranslateServiceType.GoogleV1),
        ["ln"] = new("Lingala", "ln", TranslateServiceType.GoogleV1),
        ["lt"] = new("Lithuanian", "lt"),
        ["lb"] = new("Luxembourgish", "lb", TranslateServiceType.GoogleV1),
        ["mk"] = new("Macedonian", "mk", TranslateServiceType.GoogleV1),
        ["mg"] = new("Malagasy", "mg", TranslateServiceType.GoogleV1),
        ["ms"] = new("Malay", "ms", TranslateServiceType.GoogleV1),
        ["ml"] = new("Malayalam", "ml", TranslateServiceType.GoogleV1),
        ["mt"] = new("Maltese", "mt", TranslateServiceType.GoogleV1),
        ["mi"] = new("Maori", "mi", TranslateServiceType.GoogleV1),
        ["mr"] = new("Marathi", "mr", TranslateServiceType.GoogleV1),
        ["mn"] = new("Mongolian", "mn", TranslateServiceType.GoogleV1),
        ["my"] = new("Myanmar (Burmese)", "my", TranslateServiceType.GoogleV1),
        ["nr"] = new("Ndebele (South)", "nr", TranslateServiceType.GoogleV1),
        ["ne"] = new("Nepali", "ne", TranslateServiceType.GoogleV1),

        // TODO: L: review handling
        // special handling
        ["no"] = new("Norwegian", "no"),
        ["nb"] = new("Norwegian Bokmål", "nb"),

        ["oc"] = new("Occitan", "oc", TranslateServiceType.GoogleV1),
        ["or"] = new("Odia (Oriya)", "or", TranslateServiceType.GoogleV1),
        ["om"] = new("Oromo", "om", TranslateServiceType.GoogleV1),
        ["ps"] = new("Pashto", "ps", TranslateServiceType.GoogleV1),
        ["fa"] = new("Persian", "fa", TranslateServiceType.GoogleV1),
        ["pl"] = new("Polish", "pl"),

        // special handling
        ["pt"] = new("Portuguese", "pt"),

        ["pa"] = new("Punjabi", "pa", TranslateServiceType.GoogleV1),
        ["qu"] = new("Quechua", "qu", TranslateServiceType.GoogleV1),
        ["ro"] = new("Romanian", "ro"),
        ["rn"] = new("Rundi", "rn", TranslateServiceType.GoogleV1),
        ["ru"] = new("Russian", "ru"),
        ["sm"] = new("Samoan", "sm", TranslateServiceType.GoogleV1),
        ["sg"] = new("Sango", "sg", TranslateServiceType.GoogleV1),
        ["sa"] = new("Sanskrit", "sa", TranslateServiceType.GoogleV1),
        ["gd"] = new("Scots Gaelic", "gd", TranslateServiceType.GoogleV1),
        ["sr"] = new("Serbian", "sr", TranslateServiceType.GoogleV1),
        ["st"] = new("Sesotho", "st", TranslateServiceType.GoogleV1),
        ["sn"] = new("Shona", "sn", TranslateServiceType.GoogleV1),
        ["sd"] = new("Sindhi", "sd", TranslateServiceType.GoogleV1),
        ["si"] = new("Sinhala (Sinhalese)", "si", TranslateServiceType.GoogleV1),
        ["sk"] = new("Slovak", "sk"),
        ["sl"] = new("Slovenian", "sl"),
        ["so"] = new("Somali", "so", TranslateServiceType.GoogleV1),
        ["es"] = new("Spanish", "es"),
        ["su"] = new("Sundanese", "su", TranslateServiceType.GoogleV1),
        ["sw"] = new("Swahili", "sw", TranslateServiceType.GoogleV1),
        ["ss"] = new("Swati", "ss", TranslateServiceType.GoogleV1),
        ["sv"] = new("Swedish", "sv"),
        ["tg"] = new("Tajik", "tg", TranslateServiceType.GoogleV1),
        ["ta"] = new("Tamil", "ta", TranslateServiceType.GoogleV1),
        ["tt"] = new("Tatar", "tt", TranslateServiceType.GoogleV1),
        ["te"] = new("Telugu", "te", TranslateServiceType.GoogleV1),
        ["th"] = new("Thai", "th", TranslateServiceType.GoogleV1),
        ["ti"] = new("Tigrinya", "ti", TranslateServiceType.GoogleV1),
        ["ts"] = new("Tsonga", "ts", TranslateServiceType.GoogleV1),
        ["tn"] = new("Tswana", "tn", TranslateServiceType.GoogleV1),
        ["tr"] = new("Turkish", "tr"),
        ["tk"] = new("Turkmen", "tk", TranslateServiceType.GoogleV1),
        ["ak"] = new("Twi (Akan)", "ak", TranslateServiceType.GoogleV1),
        ["uk"] = new("Ukrainian", "uk"),
        ["ur"] = new("Urdu", "ur", TranslateServiceType.GoogleV1),
        ["ug"] = new("Uyghur", "ug", TranslateServiceType.GoogleV1),
        ["uz"] = new("Uzbek", "uz", TranslateServiceType.GoogleV1),
        ["vi"] = new("Vietnamese", "vi", TranslateServiceType.GoogleV1),
        ["cy"] = new("Welsh", "cy", TranslateServiceType.GoogleV1),
        ["xh"] = new("Xhosa", "xh", TranslateServiceType.GoogleV1),
        ["yi"] = new("Yiddish", "yi", TranslateServiceType.GoogleV1),
        ["yo"] = new("Yoruba", "yo", TranslateServiceType.GoogleV1),
        ["zu"] = new("Zulu", "zu", TranslateServiceType.GoogleV1),
    };

    public TranslateLanguage(string name, string iso6391,
        TranslateServiceType supportedServices =
            TranslateServiceType.GoogleV1 | TranslateServiceType.DeepL)
    {
        // all LLMs support all languages
        supportedServices |= TranslateServiceTypeExtensions.LLMServices;

        // DeepL = DeepLX, so flag is same
        if (supportedServices.HasFlag(TranslateServiceType.DeepL))
        {
            supportedServices |= TranslateServiceType.DeepLX;
        }

        Name = name;
        ISO6391 = iso6391;
        SupportedServices = supportedServices;
    }

    public string Name { get; }

    public string ISO6391 { get; }

    public TranslateServiceType SupportedServices { get; }
}

[DataContract]
public enum TargetLanguage
{
    // Supported by Google and DeepL
    [EnumMember(Value = "ar")] Arabic,
    [EnumMember(Value = "bg")] Bulgarian,
    [EnumMember(Value = "zh-CN")] ChineseSimplified,
    [EnumMember(Value = "zh-TW")] ChineseTraditional,
    [EnumMember(Value = "cs")] Czech,
    [EnumMember(Value = "da")] Danish,
    [EnumMember(Value = "nl")] Dutch,
    [EnumMember(Value = "en-US")] EnglishAmerican,
    [EnumMember(Value = "en-GB")] EnglishBritish,
    [EnumMember(Value = "et")] Estonian,
    [EnumMember(Value = "fr-FR")] French,
    [EnumMember(Value = "fr-CA")] FrenchCanadian,
    [EnumMember(Value = "de")] German,
    [EnumMember(Value = "el")] Greek,
    [EnumMember(Value = "hu")] Hungarian,
    [EnumMember(Value = "id")] Indonesian,
    [EnumMember(Value = "it")] Italian,
    [EnumMember(Value = "ja")] Japanese,
    [EnumMember(Value = "ko")] Korean,
    [EnumMember(Value = "lt")] Lithuanian,
    [EnumMember(Value = "nb")] NorwegianBokmål,
    [EnumMember(Value = "pl")] Polish,
    [EnumMember(Value = "pt-PT")] Portuguese,
    [EnumMember(Value = "pt-BR")] PortugueseBrazilian,
    [EnumMember(Value = "ro")] Romanian,
    [EnumMember(Value = "ru")] Russian,
    [EnumMember(Value = "sk")] Slovak,
    [EnumMember(Value = "sl")] Slovenian,
    [EnumMember(Value = "es")] Spanish,
    [EnumMember(Value = "sv")] Swedish,
    [EnumMember(Value = "tr")] Turkish,
    [EnumMember(Value = "uk")] Ukrainian,

    // Only supported in Google
    [EnumMember(Value = "ab")] Abkhaz,
    [EnumMember(Value = "af")] Afrikaans,
    [EnumMember(Value = "sq")] Albanian,
    [EnumMember(Value = "am")] Amharic,
    [EnumMember(Value = "hy")] Armenian,
    [EnumMember(Value = "as")] Assamese,
    [EnumMember(Value = "ay")] Aymara,
    [EnumMember(Value = "az")] Azerbaijani,
    [EnumMember(Value = "bm")] Bambara,
    [EnumMember(Value = "ba")] Bashkir,
    [EnumMember(Value = "eu")] Basque,
    [EnumMember(Value = "be")] Belarusian,
    [EnumMember(Value = "bn")] Bengali,
    [EnumMember(Value = "bs")] Bosnian,
    [EnumMember(Value = "br")] Breton,
    [EnumMember(Value = "ca")] Catalan,
    [EnumMember(Value = "ny")] Chichewa,
    [EnumMember(Value = "cv")] Chuvash,
    [EnumMember(Value = "co")] Corsican,
    [EnumMember(Value = "hr")] Croatian,
    [EnumMember(Value = "dv")] Divehi,
    [EnumMember(Value = "dz")] Dzongkha,
    [EnumMember(Value = "eo")] Esperanto,
    [EnumMember(Value = "ee")] Ewe,
    [EnumMember(Value = "fj")] Fijian,
    [EnumMember(Value = "tl")] Tagalog,
    [EnumMember(Value = "fy")] Frisian,
    [EnumMember(Value = "ff")] Fulfulde,
    [EnumMember(Value = "gl")] Galician,
    [EnumMember(Value = "lg")] Ganda,
    [EnumMember(Value = "ka")] Georgian,
    [EnumMember(Value = "gn")] Guarani,
    [EnumMember(Value = "gu")] Gujarati,
    [EnumMember(Value = "ht")] Haitian,
    [EnumMember(Value = "ha")] Hausa,
    [EnumMember(Value = "he")] Hebrew,
    [EnumMember(Value = "hi")] Hindi,
    [EnumMember(Value = "is")] Icelandic,
    [EnumMember(Value = "ig")] Igbo,
    [EnumMember(Value = "ga")] Irish,
    [EnumMember(Value = "jv")] Javanese,
    [EnumMember(Value = "kn")] Kannada,
    [EnumMember(Value = "kk")] Kazakh,
    [EnumMember(Value = "km")] Khmer,
    [EnumMember(Value = "rw")] Kinyarwanda,
    [EnumMember(Value = "ku")] Kurdish,
    [EnumMember(Value = "ky")] Kyrgyz,
    [EnumMember(Value = "lo")] Lao,
    [EnumMember(Value = "la")] Latin,
    [EnumMember(Value = "li")] Limburgan,
    [EnumMember(Value = "ln")] Lingala,
    [EnumMember(Value = "lb")] Luxembourgish,
    [EnumMember(Value = "mk")] Macedonian,
    [EnumMember(Value = "mg")] Malagasy,
    [EnumMember(Value = "ms")] Malay,
    [EnumMember(Value = "ml")] Malayalam,
    [EnumMember(Value = "mt")] Maltese,
    [EnumMember(Value = "mi")] Maori,
    [EnumMember(Value = "mr")] Marathi,
    [EnumMember(Value = "mn")] Mongolian,
    [EnumMember(Value = "my")] Myanmar,
    [EnumMember(Value = "nr")] Ndebele,
    [EnumMember(Value = "ne")] Nepali,
    [EnumMember(Value = "oc")] Occitan,
    [EnumMember(Value = "or")] Odia,
    [EnumMember(Value = "om")] Oromo,
    [EnumMember(Value = "ps")] Pashto,
    [EnumMember(Value = "fa")] Persian,
    [EnumMember(Value = "pa")] Punjabi,
    [EnumMember(Value = "qu")] Quechua,
    [EnumMember(Value = "rn")] Rundi,
    [EnumMember(Value = "sm")] Samoan,
    [EnumMember(Value = "sg")] Sango,
    [EnumMember(Value = "sa")] Sanskrit,
    [EnumMember(Value = "gd")] ScotsGaelic,
    [EnumMember(Value = "sr")] Serbian,
    [EnumMember(Value = "st")] Sesotho,
    [EnumMember(Value = "sn")] Shona,
    [EnumMember(Value = "sd")] Sindhi,
    [EnumMember(Value = "si")] Sinhala,
    [EnumMember(Value = "so")] Somali,
    [EnumMember(Value = "su")] Sundanese,
    [EnumMember(Value = "sw")] Swahili,
    [EnumMember(Value = "ss")] Swati,
    [EnumMember(Value = "tg")] Tajik,
    [EnumMember(Value = "ta")] Tamil,
    [EnumMember(Value = "tt")] Tatar,
    [EnumMember(Value = "te")] Telugu,
    [EnumMember(Value = "th")] Thai,
    [EnumMember(Value = "ti")] Tigrinya,
    [EnumMember(Value = "ts")] Tsonga,
    [EnumMember(Value = "tn")] Tswana,
    [EnumMember(Value = "tk")] Turkmen,
    [EnumMember(Value = "ak")] Twi,
    [EnumMember(Value = "ur")] Urdu,
    [EnumMember(Value = "ug")] Uyghur,
    [EnumMember(Value = "uz")] Uzbek,
    [EnumMember(Value = "vi")] Vietnamese,
    [EnumMember(Value = "cy")] Welsh,
    [EnumMember(Value = "xh")] Xhosa,
    [EnumMember(Value = "yi")] Yiddish,
    [EnumMember(Value = "yo")] Yoruba,
    [EnumMember(Value = "zu")] Zulu,
}

public static class TargetLanguageExtensions
{
    public static string DisplayName(this TargetLanguage targetLang)
    {
        return targetLang switch
        {
            TargetLanguage.EnglishAmerican => "English (American)",
            TargetLanguage.EnglishBritish => "English (British)",
            TargetLanguage.French => "French",
            TargetLanguage.FrenchCanadian => "French (Canadian)",
            TargetLanguage.Portuguese => "Portuguese",
            TargetLanguage.PortugueseBrazilian => "Portuguese (Brazilian)",
            TargetLanguage.ChineseSimplified => "Chinese (Simplified)",
            TargetLanguage.ChineseTraditional => "Chinese (Traditional)",
            TargetLanguage.NorwegianBokmål => "Norwegian Bokmål",

            TargetLanguage.Chichewa => "Chichewa (Nyanja)",
            TargetLanguage.Ganda => "Ganda (Luganda)",
            TargetLanguage.Haitian => "Haitian Creole",
            TargetLanguage.Kurdish => "Kurdish (Kurmanji)",
            TargetLanguage.Myanmar => "Myanmar (Burmese)",
            TargetLanguage.Ndebele => "Ndebele (South)",
            TargetLanguage.Odia => "Odia (Oriya)",
            TargetLanguage.ScotsGaelic => "Scots Gaelic",
            TargetLanguage.Sinhala => "Sinhala (Sinhalese)",
            TargetLanguage.Twi => "Twi (Akan)",
            _ => targetLang.ToString()
        };
    }

    public static TranslateServiceType SupportedServiceType(this TargetLanguage targetLang)
    {
        string iso6391 = targetLang.ToISO6391();

        TranslateLanguage lang = TranslateLanguage.Langs[iso6391];

        return lang.SupportedServices;
    }

    public static string ToISO6391(this TargetLanguage targetLang)
    {
        // Get EnumMember language code
        Type type = targetLang.GetType();
        MemberInfo[] memInfo = type.GetMember(targetLang.ToString());
        object[] attributes = memInfo[0].GetCustomAttributes(typeof(EnumMemberAttribute), false);
        string enumValue = ((EnumMemberAttribute)attributes[0]).Value;

        // ISO6391 code is the first half of a hyphen
        if (enumValue != null && enumValue.Contains("-"))
        {
            return enumValue.Split('-')[0];
        }

        return enumValue;
    }
}
