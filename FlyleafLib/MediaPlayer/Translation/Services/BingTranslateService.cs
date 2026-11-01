using System.Diagnostics;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class BingTranslateService : ITranslateService
{
    private readonly BingTranslateSettings _settings;

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0";
    private readonly HttpClient _httpClient;

    private string? _srcLang;
    private string? _targetLang;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BingTranslateService(BingTranslateSettings settings)
    {
        ServiceType = settings.ServiceType;

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

    public TranslateServiceType ServiceType { get; }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        (TranslateLanguage srcLang, _) = this.TryGetLanguage(src, target);

        _srcLang = AzureTranslateService.ToSourceCode(srcLang.ISO6391, _settings);
        _targetLang = AzureTranslateService.ToTargetCode(target);
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        if (_srcLang == null || _targetLang == null)
        {
            throw new InvalidOperationException("must be initialized");
        }

        string jsonResultString = "";
        int statusCode = -1;

        try
        {
            string[] body = [text];

            string jsonRequest = JsonSerializer.Serialize(body, JsonOptions);
            using StringContent content = new(jsonRequest, Encoding.UTF8, "application/json");

            string route = $"/translate/translatetext?isEnterpriseClient=false&from={_srcLang}&to={_targetLang}";

            using HttpRequestMessage req = new(HttpMethod.Post, route);
            req.Content = content;

            using var result = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
            jsonResultString = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            var responseData = JsonSerializer.Deserialize<AzureTranslateService.MicrosoftTranslateResponse[]>(jsonResultString);

            Debug.Assert(responseData != null);
            Debug.Assert(responseData.Length == 1, "must match the size of the request array");
            Debug.Assert(responseData[0].translations.Length == 1, "must match the number of languages in 'to'");

            return responseData[0].translations[0].text;
        }
        catch (OperationCanceledException ex)
            when (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
        {
            throw;
        }
        catch (Exception ex)
        {
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
