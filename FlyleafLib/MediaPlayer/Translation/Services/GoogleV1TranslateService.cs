using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class GoogleV1TranslateService : ITranslateService
{
    internal static Dictionary<string, string> DefaultRegions { get; } = new()
    {
        ["zh"] = "zh-CN",
        ["pt"] = "pt-PT",
        ["fr"] = "fr-FR",
    };

    private readonly HttpClient _httpClient;
    private string? _srcLang;
    private string? _targetLang;
    private readonly GoogleV1TranslateSettings _settings;

    public GoogleV1TranslateService(GoogleV1TranslateSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(settings.Endpoint);
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        string iso6391 = src.ISO6391;

        // TODO: L: Allow the user to choose auto-detection?
        if (src == Language.Unknown)
        {
            throw new ArgumentException("src language are unknown");
        }

        if (src.ISO6391 == target.ToISO6391())
        {
            throw new ArgumentException("src and target language are same");
        }

        if (!TranslateLanguage.Langs.TryGetValue(iso6391, out var srcLang))
        {
            throw new ArgumentException($"src language is not supported: {src}");
        }

        if (!srcLang.SupportedServices.HasFlag(TranslateServiceType.GoogleV1))
        {
            throw new ArgumentException($"src language is not supported by GoogleV1: {src}");
        }

        _srcLang = ToSourceCode(srcLang.ISO6391);

        if (!TranslateLanguage.Langs.TryGetValue(target.ToISO6391(), out var targetLang))
        {
            throw new ArgumentException($"target language is not supported: {target}");
        }

        if (!targetLang.SupportedServices.HasFlag(TranslateServiceType.GoogleV1))
        {
            throw new ArgumentException($"target language is not supported by GoogleV1: {src}");
        }

        _targetLang = ToTargetCode(target);
    }

    private string ToSourceCode(string iso6391)
    {
        if (iso6391 == "nb")
        {
            // handle 'Norwegian Bokmål' as 'Norwegian'
            return "no";
        }

        // ref: https://cloud.google.com/translate/docs/languages?hl=en
        if (!DefaultRegions.TryGetValue(iso6391, out string? defaultRegion))
        {
            // no region languages
            return iso6391;
        }

        // has region languages
        return _settings.Regions.GetValueOrDefault(iso6391, defaultRegion);
    }

    private static string ToTargetCode(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.ChineseSimplified => "zh-CN",
            TargetLanguage.ChineseTraditional => "zh-TW",
            TargetLanguage.French => "fr-FR",
            TargetLanguage.FrenchCanadian => "fr-CA",
            TargetLanguage.Portuguese => "pt-PT",
            TargetLanguage.PortugueseBrazilian => "pt-BR",
            _ => target.ToISO6391()
        };
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        string jsonResultString;

        try
        {
            var url = $"/translate_a/single?client=gtx&sl={_srcLang}&tl={_targetLang}&dt=t&q={Uri.EscapeDataString(text)}";

            using var result = await _httpClient.GetAsync(url, token).ConfigureAwait(false);
            jsonResultString = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            result.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Cannot request to GoogleV1: {ex.Message}", ex);
        }

        try
        {
            List<string> resultTexts = new();
            using JsonDocument doc = JsonDocument.Parse(jsonResultString);
            resultTexts.AddRange(doc.RootElement[0].EnumerateArray().Select(arr => arr[0].GetString()!.Trim()));

            return string.Join(Environment.NewLine, resultTexts);
        }
        catch (Exception ex)
        {
            throw new TranslationException("Cannot parse response as JSON", ex);
        }
    }
}
