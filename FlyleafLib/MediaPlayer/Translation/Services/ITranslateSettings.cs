using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlyleafLib.Controls.WPF;

namespace FlyleafLib.MediaPlayer.Translation.Services;

public interface ITranslateSettings : INotifyPropertyChanged;

public class GoogleV1TranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    private const string DefaultEndpoint = "https://translate.googleapis.com";

    public string Endpoint
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                CmdSetDefaultEndpoint.OnCanExecuteChanged();
            }
        }
    } = DefaultEndpoint;

    [JsonIgnore]
    public RelayCommand CmdSetDefaultEndpoint => field ??= new(_ =>
    {
        Endpoint = DefaultEndpoint;
    }, _ => Endpoint != DefaultEndpoint);

    public int TimeoutMs { get; set => Set(ref field, value); } = 10000;

    public Dictionary<string, string> Regions { get; set; } = new(GoogleV1TranslateService.DefaultRegions);

    /// <summary>
    /// for Settings
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<LanguageRegions> LanguageRegions
    {
        get
        {
            if (field == null)
            {
                field = LoadLanguageRegions();

                foreach (var pref in field)
                {
                    pref.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(Services.LanguageRegions.SelectedRegionMember))
                        {
                            // Apply changes in setting
                            Regions[pref.ISO6391] = pref.SelectedRegionMember.Code;
                        }
                    };
                }
            }

            return field;
        }
    }

    private ObservableCollection<LanguageRegions> LoadLanguageRegions()
    {
        List<LanguageRegions> preferences = [
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
                    new LanguageRegionMember { Name = "Portuguese (Portugal)", Code = "pt-PT" },
                    new LanguageRegionMember { Name = "Portuguese (Brazil)", Code = "pt-BR" }
                ],
            }
        ];

        foreach (LanguageRegions p in preferences)
        {
            if (Regions.TryGetValue(p.ISO6391, out string code))
            {
                // loaded from config
                p.SelectedRegionMember = p.Regions.FirstOrDefault(r => r.Code == code);
            }
            else
            {
                // select first
                p.SelectedRegionMember = p.Regions.First();
            }
        }

        return new ObservableCollection<LanguageRegions>(preferences);
    }
}

public class DeepLTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    public string ApiKey { get; set => Set(ref field, value); }

    public int TimeoutMs { get; set => Set(ref field, value); } = 10000;
}

public class DeepLXTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    public string Endpoint { get; set => Set(ref field, value); } = "http://127.0.0.1:1188";

    public int TimeoutMs { get; set => Set(ref field, value); } = 10000;
}

public abstract class OpenAIBaseTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    protected OpenAIBaseTranslateSettings()
    {
        // ReSharper disable once VirtualMemberCallInConstructor
        Endpoint = DefaultEndpoint;
    }

    public abstract TranslateServiceType ServiceType { get; }
    public string Endpoint
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                CmdSetDefaultEndpoint.OnCanExecuteChanged();
            }
        }
    }
    [JsonIgnore]
    protected virtual bool ReuseConnection => true;

    public abstract string DefaultEndpoint { get; }

    [JsonIgnore]
    public virtual string ChatPath
    {
        get => "/v1/chat/completions";
        set => throw new NotImplementedException();
    }

    public string Model { get; set => Set(ref field, value); }

    [JsonIgnore]
    public virtual bool ModelRequired => true;

    [JsonIgnore]
    public virtual bool ReasonStripRequired => true;

    public int TimeoutMs { get; set => Set(ref field, value); } = 15000;
    public int TimeoutHealthMs { get; set => Set(ref field, value); } = 2000;

    #region LLM Parameters
    public double Temperature
    {
        get;
        set
        {
            if (value is >= 0.0 and <= 2.0)
            {
                Set(ref field, Math.Round(value, 2));
            }
        }
    } = 0.0;

    public bool TemperatureManual { get; set => Set(ref field, value); } = true;

    public double TopP
    {
        get;
        set
        {
            if (value is >= 0.0 and <= 1.0)
            {
                Set(ref field, Math.Round(value, 2));
            }
        }
    } = 1;

    public bool TopPManual { get; set => Set(ref field, value); }

    public int? MaxTokens
    {
        get;
        set => Set(ref field, value is <= 0 ? null : value);
    }

    public int? MaxCompletionTokens
    {
        get;
        set => Set(ref field, value is <= 0 ? null : value);
    }
    #endregion

    /// <summary>
    /// GetHttpClient
    /// </summary>
    /// <param name="healthCheck"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    internal virtual HttpClient GetHttpClient(bool healthCheck = false)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new TranslationConfigException(
                $"Endpoint for {ServiceType} is not configured.");
        }

        if (!healthCheck)
        {
            if (ModelRequired && string.IsNullOrWhiteSpace(Model))
            {
                throw new TranslationConfigException(
                    $"Model for {ServiceType} is not configured.");
            }
        }

        // In KoboldCpp, if this is not set, even if it is sent with Connection: close,
        // the connection will be reused and an error will occur.
        HttpMessageHandler handler = ReuseConnection ?
            new HttpClientHandler() :
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.Zero,
            };

        HttpClient client = new(handler);
        client.BaseAddress = new Uri(Endpoint);
        client.Timeout = TimeSpan.FromMilliseconds(healthCheck ? TimeoutHealthMs : TimeoutMs);
        if (!ReuseConnection)
        {
            client.DefaultRequestHeaders.ConnectionClose = true;
        }

        return client;
    }

    #region For Settings
    [JsonIgnore]
    public ObservableCollection<string> AvailableModels { get; } = new();

    [JsonIgnore]
    public string Status
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(StatusAvailable));
            }
        }
    }

    [JsonIgnore]
    public bool StatusAvailable => !string.IsNullOrEmpty(Status);

    [JsonIgnore]
    public RelayCommand CmdSetDefaultEndpoint => field ??= new(_ =>
    {
        Endpoint = DefaultEndpoint;
    }, _ => Endpoint != DefaultEndpoint);

    [JsonIgnore]
    public RelayCommand CmdCheckEndpoint => new(async void (_) =>
    {
        try
        {
            Status = "Checking...";
            await LoadModels();
            Status = "OK";
        }
        catch (Exception ex)
        {
            Status = GetErrorDetails($"NG: {ex.Message}", ex);
        }
    });

    [JsonIgnore]
    public RelayCommand CmdGetModels => new(async void (_) =>
    {
        try
        {
            Status = "Checking...";
            await LoadModels();
            Status = ""; // clear
        }
        catch (Exception ex)
        {
            Status = GetErrorDetails($"NG: {ex.Message}", ex);
        }
    });

    [JsonIgnore]
    public RelayCommand CmdHelloModel => new(async void (_) =>
    {
        Stopwatch sw = new();
        sw.Start();
        try
        {
            Status = "Waiting...";

            await OpenAIBaseTranslateService.Hello(this);

            Status = $"OK in {sw.Elapsed.TotalSeconds} secs";
        }
        catch (Exception ex)
        {
            Status = GetErrorDetails($"NG in {sw.Elapsed.TotalSeconds} secs: {ex.Message}", ex);
        }
    });

    private async Task LoadModels()
    {
        string prevModel = Model;
        AvailableModels.Clear();

        var models = await OpenAIBaseTranslateService.GetLoadedModels(this);
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }

        if (!string.IsNullOrEmpty(prevModel))
        {
            Model = AvailableModels.FirstOrDefault(m => m == prevModel);
        }
    }

    internal static string GetErrorDetails(string header, Exception ex)
    {
        StringBuilder sb = new();
        sb.Append(header);

        if (ex.Data.Contains("status_code") && (string)ex.Data["status_code"] != "-1")
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append($"status_code: {ex.Data["status_code"]}");
        }

        if (ex.Data.Contains("response") && (string)ex.Data["response"] != "")
        {
            sb.AppendLine();
            sb.Append($"response: {ex.Data["response"]}");
        }

        return sb.ToString();
    }
    #endregion
}

public class OllamaTranslateSettings : OpenAIBaseTranslateSettings
{
    public OllamaTranslateSettings()
    {
        TimeoutMs = 20000;
    }

    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.Ollama;
    [JsonIgnore]
    public override string DefaultEndpoint => "http://127.0.0.1:11434";
}

public class LMStudioTranslateSettings : OpenAIBaseTranslateSettings
{
    public LMStudioTranslateSettings()
    {
        TimeoutMs = 20000;
    }

    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.LMStudio;
    [JsonIgnore]
    public override string DefaultEndpoint => "http://127.0.0.1:1234";
    [JsonIgnore]
    public override bool ModelRequired => false;
}

public class KoboldCppTranslateSettings : OpenAIBaseTranslateSettings
{
    public KoboldCppTranslateSettings()
    {
        TimeoutMs = 20000;
    }

    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.KoboldCpp;
    [JsonIgnore]
    public override string DefaultEndpoint => "http://127.0.0.1:5001";

    // Disabled due to error when reusing connections
    [JsonIgnore]
    protected override bool ReuseConnection => false;

    [JsonIgnore]
    public override bool ModelRequired => false;
}

public class OpenAITranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.OpenAI;
    [JsonIgnore]
    public override string DefaultEndpoint => "https://api.openai.com";
    public string ApiKey { get; set => Set(ref field, value); }

    [JsonIgnore]
    public override bool ReasonStripRequired => false;

    /// <summary>
    /// GetHttpClient
    /// </summary>
    /// <param name="healthCheck"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    internal override HttpClient GetHttpClient(bool healthCheck = false)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new TranslationConfigException(
                $"API Key for {ServiceType} is not configured.");
        }

        HttpClient client = base.GetHttpClient(healthCheck);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

        return client;
    }
}

public class OpenAILikeTranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.OpenAILike;
    [JsonIgnore]
    public override string DefaultEndpoint => "https://api.openai.com";

    private const string DefaultChatPath = "/v1/chat/completions";
    public override string ChatPath
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                CmdSetDefaultChatPath.OnCanExecuteChanged();
            }
        }
    } = DefaultChatPath;

    [JsonIgnore]
    public RelayCommand CmdSetDefaultChatPath => field ??= new(_ =>
    {
        ChatPath = DefaultChatPath;
    }, _ => ChatPath != DefaultChatPath);

    [JsonIgnore]
    public override bool ModelRequired => false;
    public string ApiKey { get; set => Set(ref field, value); }

    /// <summary>
    /// GetHttpClient
    /// </summary>
    /// <param name="healthCheck"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    internal override HttpClient GetHttpClient(bool healthCheck = false)
    {
        HttpClient client = base.GetHttpClient(healthCheck);

        // optional ApiKey
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        }

        return client;
    }
}

public class ClaudeTranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.Claude;
    [JsonIgnore]
    public override string DefaultEndpoint => "https://api.anthropic.com";
    public string ApiKey { get; set => Set(ref field, value); }

    [JsonIgnore]
    public override bool ReasonStripRequired => false;

    internal override HttpClient GetHttpClient(bool healthCheck = false)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new TranslationConfigException(
                $"API Key for {ServiceType} is not configured.");
        }

        HttpClient client = base.GetHttpClient(healthCheck);
        client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        return client;
    }
}

public class LiteLLMTranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.LiteLLM;
    [JsonIgnore]
    public override string DefaultEndpoint => "http://127.0.0.1:4000";
}

public class LanguageRegionMember : NotifyPropertyChanged, IEquatable<LanguageRegionMember>
{
    public string Name { get; set; }
    public string Code { get; set; }

    public bool Equals(LanguageRegionMember other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Code == other.Code;
    }

    public override bool Equals(object obj) => obj is LanguageRegionMember o && Equals(o);

    public override int GetHashCode()
    {
        return (Code != null ? Code.GetHashCode() : 0);
    }
}

public class LanguageRegions : NotifyPropertyChanged
{
    public string ISO6391 { get; set; }
    public string Name { get; set; }
    public List<LanguageRegionMember> Regions { get; set; }

    public LanguageRegionMember SelectedRegionMember { get; set => Set(ref field, value); }
}
