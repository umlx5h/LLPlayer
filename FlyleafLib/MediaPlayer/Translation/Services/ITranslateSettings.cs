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

    public string Endpoint { get; set => Set(ref field, value); } = DefaultEndpoint;

    [JsonIgnore]
    public RelayCommand CmdSetDefaultEndpoint => new((_) =>
    {
        Endpoint = DefaultEndpoint;
    });

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
    // First request is abnormally slow on localhost, IPv6 related?
    public string Endpoint { get; set => Set(ref field, value); } = "http://127.0.0.1:1188";

    public int TimeoutMs { get; set => Set(ref field, value); } = 10000;
}

// TODO: L: share code between Ollama and OpenAI?
public class OllamaTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    public string Endpoint { get; set => Set(ref field, value); } = "http://127.0.0.1:11434";
    public string Model { get; set => Set(ref field, value); }
    public int TimeoutMs { get; set => Set(ref field, value); } = 20000;
    public int TimeoutHealthMs { get; set => Set(ref field, value); } = 2000;

    /// <summary>
    /// GetHttpClient
    /// </summary>
    /// <param name="healthCheck"></param>
    /// <returns></returns>
    /// <exception cref="TranslationConfigException"></exception>
    internal HttpClient GetHttpClient(bool healthCheck = false)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new TranslationConfigException(
                "Endpoint for Ollama is not configured.");
        }

        if (!healthCheck)
        {
            if (string.IsNullOrWhiteSpace(Model))
            {
                throw new TranslationConfigException(
                    "Model for Ollama is not configured.");
            }
        }

        HttpClient client = new();
        client.BaseAddress = new Uri(Endpoint);
        client.Timeout = TimeSpan.FromMilliseconds(healthCheck ? TimeoutHealthMs : TimeoutMs);

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
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG: {ex.Message}", ex);
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
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG: {ex.Message}", ex);
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

            await OllamaTranslateService.Hello(this);

            Status = $"OK in {sw.Elapsed.TotalSeconds} secs";
        }
        catch (Exception ex)
        {
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG in {sw.Elapsed.TotalSeconds} secs: {ex.Message}", ex);
        }
    });

    private async Task LoadModels()
    {
        string prevModel = Model;
        AvailableModels.Clear();

        var models = await OllamaTranslateService.GetLoadedModels(this);
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }

        if (!string.IsNullOrEmpty(prevModel))
        {
            Model = AvailableModels.FirstOrDefault(m => m == prevModel);
        }
    }
    #endregion
}

public abstract class OpenAIBaseTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    [JsonIgnore]
    public abstract TranslateServiceType ServiceType { get; }
    public abstract string Endpoint { get; set; }
    [JsonIgnore]
    public abstract string DefaultEndpoint { get; }
    public string Model { get; set => Set(ref field, value); }
    public int TimeoutMs { get; set => Set(ref field, value); } = 15000;
    public int TimeoutHealthMs { get; set => Set(ref field, value); } = 2000;

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
            if (string.IsNullOrWhiteSpace(Model))
            {
                throw new TranslationConfigException(
                    $"Model for {ServiceType} is not configured.");
            }
        }

        HttpClient client = new();
        client.BaseAddress = new Uri(Endpoint);
        client.Timeout = TimeSpan.FromMilliseconds(healthCheck ? TimeoutHealthMs : TimeoutMs);

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
    public RelayCommand CmdSetDefaultEndpoint => new((_) =>
    {
        Endpoint = DefaultEndpoint;
    });

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
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG: {ex.Message}", ex);
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
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG: {ex.Message}", ex);
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
            Status = OpenAIBaseTranslateSettings.GetErrorDetails($"NG in {sw.Elapsed.TotalSeconds} secs: {ex.Message}", ex);
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

public class LMStudioTranslateSettings : OpenAIBaseTranslateSettings
{
    public LMStudioTranslateSettings()
    {
        TimeoutMs = 20000;
    }

    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.LMStudio;
    private const string _defaultEndpoint = "http://127.0.0.1:1234";
    [JsonIgnore]
    public override string DefaultEndpoint => _defaultEndpoint;
    public override string Endpoint { get; set => Set(ref field, value); } = _defaultEndpoint;
}

public class OpenAITranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.OpenAI;
    private const string _defaultEndpoint = "https://api.openai.com";
    [JsonIgnore]
    public override string DefaultEndpoint => _defaultEndpoint;
    public override string Endpoint { get; set => Set(ref field, value); } = _defaultEndpoint;
    public string ApiKey { get; set => Set(ref field, value); }

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

public class ClaudeTranslateSettings : OpenAIBaseTranslateSettings
{
    [JsonIgnore]
    public override TranslateServiceType ServiceType => TranslateServiceType.Claude;
    private const string _defaultEndpoint = "https://api.anthropic.com";
    [JsonIgnore]
    public override string DefaultEndpoint => _defaultEndpoint;
    public override string Endpoint { get; set => Set(ref field, value); } = _defaultEndpoint;
    public string ApiKey { get; set => Set(ref field, value); }

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

public class LanguageRegionMember : NotifyPropertyChanged
{
    public string Name { get; set; }
    public string Code { get; set; }

    public override bool Equals(object obj)
    {
        if (obj is not LanguageRegionMember region)
            return false;

        return region.Code == Code;
    }

    public override int GetHashCode() => Code.GetHashCode();
}

public class LanguageRegions : NotifyPropertyChanged
{
    public string ISO6391 { get; set; }
    public string Name { get; set; }
    public List<LanguageRegionMember> Regions { get; set; }

    public LanguageRegionMember SelectedRegionMember { get; set => Set(ref field, value); }
}
