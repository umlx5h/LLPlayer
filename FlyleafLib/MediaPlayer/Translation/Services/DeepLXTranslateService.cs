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
        _settings = settings;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(settings.Endpoint);
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        string iso6391 = src.ISO6391;

        if (src == Language.Unknown)
        {
            throw new ArgumentException("src language are unknown");
        }

        // Exception for same language
        if (src.ISO6391 == target.ToISO6391())
        {
            throw new ArgumentException("src and target language are same");
        }

        if (!TranslateLanguage.Langs.TryGetValue(iso6391, out var srcLang))
        {
            throw new ArgumentException($"src language is not supported: {src}", nameof(src));
        }

        if (!srcLang.SupportedServices.HasFlag(TranslateServiceType.DeepLX))
        {
            throw new ArgumentException($"src language is not supported by DeepLX: {src}", nameof(src));
        }

        _srcLang = ToSourceCode(srcLang.ISO6391);

        if (!TranslateLanguage.Langs.TryGetValue(target.ToISO6391(), out var targetLang))
        {
            throw new ArgumentException($"target language is not supported: {target}", nameof(target));
        }

        if (!targetLang.SupportedServices.HasFlag(TranslateServiceType.DeepLX))
        {
            throw new ArgumentException($"target language is not supported by DeepLX: {target}", nameof(target));
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

        string jsonResultString;

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
        catch (Exception ex)
        {
            // TODO: k: should handle cancellation
            throw new TranslationException($"Cannot request to DeepL: {ex.Message}", ex);
        }

        try
        {
            DeepLXTranslateResult? responseData = JsonSerializer.Deserialize<DeepLXTranslateResult>(jsonResultString);
            return responseData!.Data;
        }
        catch (Exception ex)
        {
            throw new TranslationException("Cannot parse response as JSON", ex);
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
