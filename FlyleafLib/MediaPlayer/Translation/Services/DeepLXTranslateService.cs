using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class DeepLXTranslateService : ITranslateService
{
    private readonly HttpClient _httpClient;
    private string? _srcLang;
    private string? _targetLang;
    private readonly DeepLXTranslateSettings _settings;

    public DeepLXTranslateService(DeepLXTranslateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new TranslationConfigException(
                "Endpoint for the DeepLX translation is not set. Please set it from the settings.");
        }

        _settings = settings;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(settings.Endpoint);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        string iso6391 = src.ISO6391;

        if (src == Language.Unknown)
        {
            throw new TranslationConfigException("src language are unknown");
        }

        // Exception for same language
        if (src.ISO6391 == target.ToISO6391())
        {
            throw new TranslationConfigException("src and target language are same");
        }

        if (!TranslateLanguage.Langs.TryGetValue(iso6391, out var srcLang))
        {
            throw new TranslationConfigException($"src language is not supported: {src.TopEnglishName}");
        }

        if (!srcLang.SupportedServices.HasFlag(TranslateServiceType.DeepLX))
        {
            throw new TranslationConfigException($"src language is not supported by DeepLX: {src.TopEnglishName}");
        }

        _srcLang = ToSourceCode(srcLang.ISO6391);

        if (!TranslateLanguage.Langs.TryGetValue(target.ToISO6391(), out var targetLang))
        {
            throw new TranslationConfigException($"target language is not supported: {target.ToString()}");
        }

        if (!targetLang.SupportedServices.HasFlag(TranslateServiceType.DeepLX))
        {
            throw new TranslationConfigException($"target language is not supported by DeepLX: {target.ToString()}");
        }

        _targetLang = ToTargetCode(target);
    }

    private string ToSourceCode(string iso6391)
    {
        return DeepLTranslateService.ToSourceCode(iso6391);
    }

    private string ToTargetCode(TargetLanguage target)
    {
        return DeepLTranslateService.ToTargetCode(target);
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        if (_srcLang == null || _targetLang == null)
        {
            throw new InvalidOperationException("must be initialized");
        }

        string jsonResultString = "";

        try
        {
            DeepLXTranslateRequest requestBody = new()
            {
                SourceLang = _srcLang,
                TargetLang = _targetLang,
                Text = text
            };

            string jsonRequest = JsonSerializer.Serialize(requestBody);
            using StringContent content = new(jsonRequest, Encoding.UTF8, "application/json");

            using var result = await _httpClient.PostAsync("/translate", content, token).ConfigureAwait(false);
            jsonResultString = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            result.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException ex)
            when (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Cannot request to DeepL: {ex.Message}", ex)
            {
                Data =
                {
                    ["response"] = jsonResultString
                }
            };
        }

        try
        {
            DeepLXTranslateResult? responseData = JsonSerializer.Deserialize<DeepLXTranslateResult>(jsonResultString);
            return responseData!.Data;
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Cannot parse response as JSON: {ex.Message}", ex)
            {
                Data =
                {
                    ["response"] = jsonResultString
                }
            };
        }
    }

    private class DeepLXTranslateRequest
    {
        [JsonPropertyName("text")]
        public required string Text { get; set; }

        [JsonPropertyName("source_lang")]
        public string? SourceLang { get; set; }

        [JsonPropertyName("target_lang")]
        public required string TargetLang { get; set; }
    }

    private class DeepLXTranslateResult
    {
        [JsonPropertyName("alternatives")]
        public string[] Alternatives { get; set; }

        [JsonPropertyName("data")]
        public string Data { get; set; }

        [JsonPropertyName("source_lang")]
        public string SourceLang { get; set; }

        [JsonPropertyName("target_lang")]
        public string TargetLang { get; set; }
    }
}
