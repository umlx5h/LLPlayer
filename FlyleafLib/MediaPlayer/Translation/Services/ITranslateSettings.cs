using FlyleafLib.Controls.WPF;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;

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
}

public class DeepLXTranslateSettings : NotifyPropertyChanged, ITranslateSettings
{
    // First request is abnormally slow on localhost, IPv6 related?
    public string Endpoint
    {
        get;
        set => Set(ref field, value);
    } = "http://127.0.0.1:11188";
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
