using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public abstract class MicrosoftTranslateServiceBase : ITranslateService
{
    #region Region Definition
    internal static Dictionary<string, string> DefaultRegions { get; } = new()
    {
        ["zh"] = "zh-Hans",
        ["fr"] = "fr",
        ["pt"] = "pt",
    };

    internal static List<LanguageRegions> Regions =>
    [
        new()
        {
            Name = "Chinese",
            ISO6391 = "zh",
            Regions =
            [
                new LanguageRegionMember { Name = "Chinese (Simplified)", Code = "zh-Hans" },
                new LanguageRegionMember { Name = "Chinese (Traditional)", Code = "zh-Hant" }
            ],
        },
        new()
        {
            Name = "French",
            ISO6391 = "fr",
            Regions =
            [
                new LanguageRegionMember { Name = "French (French)", Code = "fr" },
                new LanguageRegionMember { Name = "French (Canadian)", Code = "fr-ca" }
            ],
        },
        new()
        {
            Name = "Portuguese",
            ISO6391 = "pt",
            Regions =
            [
                new LanguageRegionMember { Name = "Portuguese (Brazil)", Code = "pt" },
                new LanguageRegionMember { Name = "Portuguese (Portugal)", Code = "pt-pt" }
            ],
        }
    ];
    #endregion

    private readonly MicrosoftTranslateSettings _settings;

    protected const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0";
    private readonly HttpClient _httpClient;

    private string? _srcLang;
    private string? _targetLang;

    private volatile Task<string>? _accessToken;
    private readonly Lock _accessTokenLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    protected MicrosoftTranslateServiceBase(MicrosoftTranslateSettings settings)
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

        _srcLang = ToSourceCode(srcLang.ISO6391);
        _targetLang = ToTargetCode(target);
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        if (_srcLang == null || _targetLang == null)
        {
            throw new InvalidOperationException("must be initialized");
        }

        bool retried = false;

    RETRY_401:

        string jsonResultString = "";
        int statusCode = -1;

        try
        {
            Task<string> accessTokenTask = GetAccessTokenTask(token);
            string accessToken = await accessTokenTask.WaitAsync(token).ConfigureAwait(false);

            MicrosoftTranslateRequest[] body = [new() { Text = text }];

            string jsonRequest = JsonSerializer.Serialize(body, JsonOptions);
            using StringContent content = new(jsonRequest, Encoding.UTF8, "application/json");

            string route = $"/translate?api-version=3.0&from={_srcLang}&to={_targetLang}";

            using HttpRequestMessage req = new(HttpMethod.Post, route);
            req.Headers.Add("Authorization", $"Bearer {accessToken}");
            req.Content = content;

            using var result = await _httpClient.SendAsync(req, token).ConfigureAwait(false);
            jsonResultString = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (result.StatusCode == HttpStatusCode.Unauthorized && !retried)
            {
                retried = true;
                lock (_accessTokenLock)
                {
                    // recreate accessToken once
                    if (_accessToken == accessTokenTask)
                    {
                        _accessToken = null;
                    }
                }

                goto RETRY_401;
            }

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            MicrosoftTranslateResponse[]? responseData = JsonSerializer.Deserialize<MicrosoftTranslateResponse[]>(jsonResultString);

            Debug.Assert(responseData != null);
            Debug.Assert(responseData.Length == 1, "must match the size of the request array");
            Debug.Assert(responseData[0].translations.Length == 1, "must match the number of languages in 'to'");

            return responseData[0].translations[0].text;
        }
        catch (OperationCanceledException ex)
            when (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
        {
            _accessToken = null;
            throw;
        }
        catch (Exception ex)
        {
            _accessToken = null;
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

    protected abstract Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken token);

    protected static async Task<string> ReadTokenResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"cannot get token with {response.StatusCode}: {content}");
        }

        if (string.IsNullOrEmpty(content) || content.Count('.') != 2)
        {
            throw new InvalidOperationException($"invalid token: {content}");
        }

        return content;
    }

    // ref: https://learn.microsoft.com/en-us/azure/ai-services/translator/language-support
    private string ToSourceCode(string iso6391)
    {
        if (!DefaultRegions.TryGetValue(iso6391, out string? defaultRegion))
        {
            return iso6391 switch
            {
                "lg" => "lug", // Ganda
                "mn" => "mn-Cyrl", // Mongolian
                "ny" => "nya", // Chichewa
                "rn" => "run", // Rundi
                "sr" => "sr-Latn", // Serbian

                "no" => "nb", // Norwegian Bokmal

                _ => iso6391
            };
        }

        return _settings.Regions.GetValueOrDefault(iso6391, defaultRegion);
    }

    private static string ToTargetCode(TargetLanguage target)
    {
        return target switch
        {
            TargetLanguage.ChineseSimplified => "zh-Hans",
            TargetLanguage.ChineseTraditional => "zh-Hant",
            TargetLanguage.French => "fr",
            TargetLanguage.FrenchCanadian => "fr-ca",
            TargetLanguage.Portuguese => "pt-pt",
            TargetLanguage.PortugueseBrazilian => "pt",

            TargetLanguage.Ganda => "lug",
            TargetLanguage.Mongolian => "mn-Cyrl",
            TargetLanguage.Chichewa => "nya",
            TargetLanguage.Rundi => "run",
            TargetLanguage.Serbian => "sr-Latn",

            _ => target.ToISO6391()
        };
    }

    private Task<string> GetAccessTokenTask(CancellationToken token)
    {
        Task<string>? accessTokenTask = _accessToken;
        if (accessTokenTask != null)
        {
            return accessTokenTask;
        }

        lock (_accessTokenLock)
        {
            _accessToken ??= GetAccessTokenAsync(_httpClient, token);
            return _accessToken;
        }
    }

    private class MicrosoftTranslateRequest
    {
        public required string Text { get; init; }
    }

    private class MicrosoftTranslateResponse
    {
        public required Translation[] translations { get; set; }
    }

    private class Translation
    {
        public required string text { get; set; }
        public required string to { get; set; }
    }
}
