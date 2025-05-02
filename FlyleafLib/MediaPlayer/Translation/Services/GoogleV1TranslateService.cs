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
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new TranslationConfigException(
                $"Endpoint for {ServiceType} is not configured.");
        }

        _settings = settings;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(settings.Endpoint);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
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
