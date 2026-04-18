using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class GoogleV1TranslateService : ITranslateService
{
    #region Region Definition
    internal static Dictionary<string, string> DefaultRegions { get; } = new()
    {
        ["zh"] = "zh-CN",
        ["fr"] = "fr-FR",
        ["pt"] = "pt-BR",
    };

    internal static List<LanguageRegions> Regions =>
    [
        new()
        {
            Name = "Chinese",
            ISO6391 = "zh",
            Regions =
            [
                // priority to the above
                new LanguageRegionMember { Name = "Chinese (Simplified)", Code = "zh-CN" },
                new LanguageRegionMember { Name = "Chinese (Traditional)", Code = "zh-TW" }
            ],
        },
        new()
        {
            Name = "French",
            ISO6391 = "fr",
            Regions =
            [
                new LanguageRegionMember { Name = "French (French)", Code = "fr-FR" },
                new LanguageRegionMember { Name = "French (Canadian)", Code = "fr-CA" }
            ],
        },
        new()
        {
            Name = "Portuguese",
            ISO6391 = "pt",
            Regions =
            [
                new LanguageRegionMember { Name = "Portuguese (Brazil)", Code = "pt-BR" },
                new LanguageRegionMember { Name = "Portuguese (Portugal)", Code = "pt-PT" }
            ],
        }
    ];
    #endregion

    private readonly HttpClient _httpClient;
    private string? _srcLang;
    private string? _targetLang;
    private readonly GoogleV1TranslateSettings _settings;

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";

    public GoogleV1TranslateService(GoogleV1TranslateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new TranslationConfigException(
                $"Endpoint for {ServiceType} is not configured.");
        }

        _settings = settings;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(settings.Endpoint),
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public TranslateServiceType ServiceType => TranslateServiceType.GoogleV1;

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        (TranslateLanguage srcLang, _) = this.TryGetLanguage(src, target);

        _srcLang = ToSourceCode(srcLang.ISO6391);
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

            TargetLanguage.NorwegianBokmål => "no", // nb -> no

            _ => target.ToISO6391()
        };
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        string jsonResultString = "";
        int statusCode = -1;

        try
        {
            var url = $"/translate_a/single?client=gtx&sl={_srcLang}&tl={_targetLang}&dt=t&q={Uri.EscapeDataString(text)}";

            using var result = await _httpClient.GetAsync(url, token).ConfigureAwait(false);
            jsonResultString = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            List<string> resultTexts = new();
            using JsonDocument doc = JsonDocument.Parse(jsonResultString);
            resultTexts.AddRange(doc.RootElement[0].EnumerateArray().Select(arr => arr[0].GetString()!.Trim()));

            return string.Join(Environment.NewLine, resultTexts);
        }
        // Distinguish between timeout and cancel errors
        catch (OperationCanceledException ex)
            when (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
        {
            // cancel
            throw;
        }
        catch (Exception ex)
        {
            // timeout and other error
            throw new TranslationException($"Cannot request to {ServiceType}: {ex.Message}", ex)
            {
                Data =
                {
                    ["status_code"] = statusCode.ToString(),
                    ["response"] = jsonResultString
                }
            };
        }
    }
}
