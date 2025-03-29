using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaPlayer.Translation.Services;
using LLPlayer.Extensions;
using MaterialDesignThemes.Wpf;
using Vortice.Mathematics;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Size = System.Windows.Size;

namespace LLPlayer.Services;

public class AppConfig : Bindable
{
    private FlyleafManager FL;

    public void Initialize(FlyleafManager fl)
    {
        FL = fl;
        Loaded = true;

        Subs.Initialize(this, fl);
    }

    /// <summary>
    /// State to skip the setter run when reading JSON
    /// </summary>
    [JsonIgnore]
    public bool Loaded { get; private set; }

    public void FlyleafHostLoaded()
    {
        Subs.FlyleafHostLoaded();

        // Ensure that FlyeafHost reflects the configuration values when restoring the configuration.
        FL.FlyleafHost!.ActivityTimeout = FL.Config.ActivityTimeout;
    }

    public AppConfigSubs Subs { get; set => Set(ref field, value); } = new();

    public static AppConfig Load(string path)
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), GetJsonSerializerOptions())!;

        return config;
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, GetJsonSerializerOptions()));

        Subs.SaveAfter();
    }

    public AppConfigTheme Theme { get; set => Set(ref field, value); } = new();

    [JsonIgnore]
    public double ScreenWidth
    {
        private get;
        set => Set(ref field, value);
    }

    // Video Screen Height (including black background), without titlebar height
    [JsonIgnore]
    public double ScreenHeight
    {
        internal get;
        set
        {
            if (Set(ref field, value))
            {
                Subs.UpdateSubsConfig();
            }
        }
    }

    public bool IsDarkTitlebar { get; set => Set(ref field, value); } = true;

    public int ActivityTimeout
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (Loaded && FL.FlyleafHost != null)
                {
                    FL.FlyleafHost.ActivityTimeout = value;
                }
            }
        }
    } = 1200;

    public bool ShowSidebar { get; set => Set(ref field, value); } = true;

    [JsonIgnore]
    public bool ShowDebug
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                FL.PlayerConfig.Player.Stats = value;
            }
        }
    }


    #region FlyleafBar
    public bool SeekBarShowOnlyMouseOver { get; set => Set(ref field, value); } = false;
    public int SeekBarFadeInTimeMs { get; set => Set(ref field, value); } = 80;
    public int SeekBarFadeOutTimeMs { get; set => Set(ref field, value); } = 150;
    #endregion

    #region Mouse
    public bool MouseSingleClickToPlay { get; set => Set(ref field, value); } = true;
    public bool MouseDoubleClickToFullScreen { get; set => Set(ref field, value); }
    public bool MouseWheelToVolumeUpDown { get; set => Set(ref field, value); } = true;
    #endregion

    // TODO: L: should be move to AppConfigSubs?
    public bool SidebarLeft
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(SidebarFlowDirection));
            }
        }
    }

    [JsonIgnore]
    public FlowDirection SidebarFlowDirection => !SidebarLeft ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

    public int SidebarWidth { get; set => Set(ref field, value); } = 300;

    public int SidebarSubPadding { get; set => Set(ref field, value); } = 5;

    [JsonIgnore]
    public bool SidebarShowSecondary { get; set => Set(ref field, value); }

    public bool SidebarShowOriginalText { get; set => Set(ref field, value); }

    public bool SidebarTextMask { get; set => Set(ref field, value); }

    public string SidebarFontFamily { get; set => Set(ref field, value); } = "Segoe UI";

    public double SidebarFontSize { get; set => Set(ref field, value); } = 16;

    public string SidebarFontWeight { get; set => Set(ref field, value); } = FontWeights.Normal.ToString();

    public static JsonSerializerOptions GetJsonSerializerOptions()
    {
        Dictionary<string, Type> typeMappingMenuAction = new()
        {
            { nameof(ClipboardMenuAction), typeof(ClipboardMenuAction) },
            { nameof(ClipboardAllMenuAction), typeof(ClipboardAllMenuAction) },
            { nameof(SearchMenuAction), typeof(SearchMenuAction) },
        };

        Dictionary<string, Type> typeMappingTranslateSettings = new()
        {
            { nameof(GoogleV1TranslateSettings), typeof(GoogleV1TranslateSettings) },
            { nameof(DeepLTranslateSettings), typeof(DeepLTranslateSettings) },
            { nameof(DeepLXTranslateSettings), typeof(DeepLXTranslateSettings) },
        };

        JsonSerializerOptions jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(),
                // TODO: L: should separate converters for different types of config?
                new JsonInterfaceConcreteConverter<IMenuAction>(typeMappingMenuAction),
                new JsonInterfaceConcreteConverter<ITranslateSettings>(typeMappingTranslateSettings),
                new ColorHexJsonConverter()
            }
        };

        return jsonOptions;
    }
}

public class AppConfigSubs : Bindable
{
    [JsonIgnore]
    private FlyleafManager FL;

    private AppConfig _rootConfig;

    [JsonIgnore]
    public bool Loaded { get; private set; }

    public void Initialize(AppConfig rootConfig, FlyleafManager fl)
    {
        _rootConfig = rootConfig;
        FL = fl;
        Loaded = true;

        // Initialize the size of secondary subtitles to the same size as the primary at startup
        SubsFontSize2 = SubsFontSize;
        // Save the initial value of the position for reset.
        _subsPositionInitial = SubsPosition;

        // register event handler
        var pSubsAutoCopy = SubsAutoTextCopy;
        SubsAutoTextCopy = false;
        SubsAutoTextCopy = pSubsAutoCopy;
    }

    public void FlyleafHostLoaded()
    {
        Viewport = FL.Player.renderer.GetViewport;

        FL.Player.renderer.ViewportChanged += (sender, args) =>
        {
            Utils.UIIfRequired(() =>
            {
                Viewport = FL.Player.renderer.GetViewport;
            });
        };
    }

    public void SaveAfter()
    {
        // Update initial value
        _subsPositionInitial = SubsPosition;
    }

    [JsonIgnore]
    public Viewport Viewport
    {
        get;
        private set
        {
            var prev = Viewport;
            if (Set(ref field, value))
            {
                if ((int)prev.Width != (int)value.Width)
                {
                    // update font size if width changed
                    OnPropertyChanged(nameof(SubsFontSizeFix));
                    OnPropertyChanged(nameof(SubsFontSize2Fix));
                }

                if ((int)prev.Height != (int)value.Height ||
                    (int)prev.Y != (int)value.Y)
                {
                    // update font margin/distance if height/Y changed
                    UpdateSubsConfig();
                }
            }
        }
    }

    public string SubsFontFamily { get; set => Set(ref field, value); } = "Segoe UI";

    // Primary Subtitle Size
    public double SubsFontSize
    {
        get;
        set
        {
            if (value <= 0)
            {
                return;
            }

            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(SubsFontSizeFix));
            }
        }
    } = 44;

    [JsonIgnore]
    public double SubsFontSizeFix => GetFixFontSize(SubsFontSize);

    // Secondary Subtitle Size
    [JsonIgnore]
    public double SubsFontSize2
    {
        get;
        set
        {
            if (value <= 0)
            {
                return;
            }

            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(SubsFontSize2Fix));
            }
        }
    }

    [JsonIgnore]
    public double SubsFontSize2Fix => GetFixFontSize(SubsFontSize2);

    private double GetFixFontSize(double fontSize)
    {
        double scaleFactor = Viewport.Width / 1920;
        double size = fontSize * scaleFactor;
        if (size > 0)
        {
            return size;
        }

        return fontSize;
    }

    public Color SubsFontColor { get; set => Set(ref field, value); } = Colors.White;

    public string SubsFontStretch { get; set => Set(ref field, value); } = FontStretches.Normal.ToString();

    public string SubsFontWeight { get; set => Set(ref field, value); } = FontWeights.Bold.ToString();

    public string SubsFontStyle { get; set => Set(ref field, value); } = FontStyles.Normal.ToString();

    [JsonIgnore]
    public Size SubsPanelSize
    {
        private get;
        set
        {
            if (Set(ref field, value))
            {
                UpdateSubsMargin();
            }
        }
    }

    private bool _isSubsOverflowBottom = true;

    [JsonIgnore]
    public Thickness SubsMargin
    {
        get;
        set => Set(ref field, value);
    }

    private double _subsPositionInitial;
    public void ResetSubsPosition()
    {
        SubsPosition = _subsPositionInitial;
    }

    #region Offsets

    public double SubsPositionOffset { get; set => Set(ref field, value); } = 2.0;
    public int SubsFontSizeOffset { get; set => Set(ref field, value); } = 2;
    public double SubsBitmapScaleOffset { get; set => Set(ref field, value); } = 4;
    public double SubsDistanceOffset { get; set => Set(ref field, value); } = 5;

    #endregion

    // -25%-150%
    // Allow some going up and down from ViewPort
    public double SubsPosition
    {
        get;
        set
        {
            if (_isSubsOverflowBottom && field < value)
            {
                // Prohibit going further down when it overflows below.
                return;
            }

            if (value < -25.0 || value > 150.0)
            {
                return;
            }

            if (Set(ref field, value))
            {
                UpdateSubsMargin();
            }
        }
    } = 85.0;

    public SubPositionAlignment SubsPositionAlignment
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                UpdateSubsConfig();
            }
        }
    } = SubPositionAlignment.Center;

    public SubPositionAlignment SubsPositionAlignmentWhenDual
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                UpdateSubsConfig();
            }
        }
    } = SubPositionAlignment.Top;

    // 0%-100%
    public double SubsFixOverflowMargin
    {
        get;
        set
        {
            if (value < 0.0 || value > 100.0)
            {
                return;
            }

            Set(ref field, value);
        }
    } = 3.0;

    [JsonIgnore]
    public double SubsDistanceFix { get; set => Set(ref field, value); }

    public double SubsDistance
    {
        get;
        set
        {
            if (value < 1.0)
            {
                return;
            }

            if (Set(ref field, value))
            {
                UpdateSubsDistance();
            }
        }
    } = 16;

    public double SubsSeparatorMaxWidth { get; set => Set(ref field, value); } = 280;
    public double SubsSeparatorOpacity
    {
        get;
        set
        {
            if (value < 0.0 || value > 1.0)
            {
                return;
            }

            Set(ref field, value);
        }
    } = 0.3;

    public double SubsWidthPercentage
    {
        get;
        set
        {
            if (value < 1.0 || value > 100.0)
            {
                return;
            }

            Set(ref field, value);
        }
    } = 66.0;

    public bool SubsIgnoreLineBreak { get; set => Set(ref field, value); }

    public Color SubsStrokeColor { get; set => Set(ref field, value); } = Colors.Black;

    public double SubsStrokeThickness { get; set => Set(ref field, value); } = 2.8;

    internal void UpdateSubsConfig()
    {
        if (!Loaded)
            return;

        UpdateSubsDistance();
        UpdateSubsMargin();
    }

    private void UpdateSubsDistance()
    {
        if (!Loaded)
            return;

        float scaleFactor = Viewport.Height / 1080;
        double newDistance = SubsDistance * scaleFactor;

        SubsDistanceFix = newDistance;
    }

    private void UpdateSubsMargin()
    {
        if (!Loaded)
            return;

        // Set the margin from the top based on Viewport, not Window
        // Allow going above or below the Viewport
        float offset = Viewport.Y;
        float height = Viewport.Height;

        double marginTop = height * (SubsPosition / 100.0);
        double marginTopFix = marginTop + offset;

        // Adjustment for vertical alignment of subtitles
        SubPositionAlignment alignment = SubsPositionAlignment;
        if (FL.Player.Subtitles[0].Enabled && FL.Player.Subtitles[1].Enabled)
        {
            alignment = SubsPositionAlignmentWhenDual;
        }

        if (alignment == SubPositionAlignment.Center)
        {
            marginTopFix -= SubsPanelSize.Height / 2;
        }
        else if (alignment == SubPositionAlignment.Bottom)
        {
            marginTopFix -= SubsPanelSize.Height;
        }

        // Corrects for off-screen subtitles if they are detected.
        marginTopFix = FixOverflowSubsPosition(marginTopFix);

        SubsMargin = new Thickness(SubsMargin.Left, marginTopFix, SubsMargin.Right, SubsMargin.Bottom);
    }

    /// <summary>
    /// Detects whether subtitles are placed off-screen and corrects them if they appear
    /// </summary>
    /// <param name="marginTop"></param>
    /// <returns></returns>
    private double FixOverflowSubsPosition(double marginTop)
    {
        double subHeight = SubsPanelSize.Height;

        double bottomMargin = Viewport.Height * (SubsFixOverflowMargin / 100.0);

        if (subHeight + marginTop + bottomMargin > _rootConfig.ScreenHeight)
        {
            // It overflowed, so fix it.
            _isSubsOverflowBottom = true;
            double fixedMargin = _rootConfig.ScreenHeight - subHeight - bottomMargin;
            return fixedMargin;
        }

        _isSubsOverflowBottom = false;
        return marginTop;
    }

    public bool SubsExportUTF8WithBom { get; set => Set(ref field, value); } = true;

    public bool SubsAutoTextCopy
    {
        get;
        set
        {
            if (Set(ref field, value) && Loaded)
            {
                if (value)
                {
                    FL.Player.Subtitles[0].Data.PropertyChanged += SubtitleTextOnPropertyChanged;
                    FL.Player.Subtitles[1].Data.PropertyChanged += SubtitleTextOnPropertyChanged;
                }
                else
                {
                    FL.Player.Subtitles[0].Data.PropertyChanged -= SubtitleTextOnPropertyChanged;
                    FL.Player.Subtitles[1].Data.PropertyChanged -= SubtitleTextOnPropertyChanged;
                }
            }
        }
    }

    public SubAutoTextCopyTarget SubsAutoTextCopyTarget { get; set => Set(ref field, value); } = SubAutoTextCopyTarget.Primary;

    private void SubtitleTextOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubsData.Text))
            return;

        switch (SubsAutoTextCopyTarget)
        {
            case SubAutoTextCopyTarget.All:
                if (FL.Player.Subtitles[0].Data.Text != "" ||
                    FL.Player.Subtitles[1].Data.Text != "")
                {
                    FL.Action.CmdSubsTextCopy.Execute(true);
                }
                break;
            case SubAutoTextCopyTarget.Primary:
                if (FL.Player.Subtitles[0].Data == sender && FL.Player.Subtitles[0].Data.Text != "")
                {
                    FL.Action.CmdSubsPrimaryTextCopy.Execute(true);
                }
                break;
            case SubAutoTextCopyTarget.Secondary:
                if (FL.Player.Subtitles[1].Data == sender && FL.Player.Subtitles[1].Data.Text != "")
                {
                    FL.Action.CmdSubsSecondaryTextCopy.Execute(true);
                }
                break;
        }
    }

    public WordClickAction WordClickActionMethod { get; set => Set(ref field, value); }
    public bool WordCopyOnSelected { get; set => Set(ref field, value); } = true;
    public bool WordLastSearchOnSelected { get; set => Set(ref field, value); } = true;

    public ModifierKeys WordLastSearchOnSelectedModifier { get; set => Set(ref field, value); } = ModifierKeys.Control;
    public ObservableCollection<IMenuAction> WordMenuActions { get; set => Set(ref field, value); } = new(
    [
        new ClipboardMenuAction(),
        new ClipboardAllMenuAction(),
        new SearchMenuAction{ Title = "Search Google", Url = "https://www.google.com/search?q=%w" },
        new SearchMenuAction{ Title = "Search Wiktionary", Url = "https://en.wiktionary.org/wiki/Special:Search?search=%w&go=Look+up" },
        new SearchMenuAction{ Title = "Search Longman", Url = "https://www.ldoceonline.com/search/english/direct/?q=%w" },
    ]);
    public string? PDICPipeExecutablePath { get; set => Set(ref field, value); }
}

public enum SubAutoTextCopyTarget
{
    Primary,
    Secondary,
    All
}

public enum SubPositionAlignment
{
    Top,    // This is useful for dual subs because primary sub position is stayed
    Center, // Same as bitmap subs
    Bottom  // Normal video players use this
}

public class AppConfigTheme : Bindable
{
    private readonly PaletteHelper _paletteHelper = new();

    public Color PrimaryColor
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Theme cur = _paletteHelper.GetTheme();
                cur.SetPrimaryColor(value);
                _paletteHelper.SetTheme(cur);
            }
        }
    } = (Color)ColorConverter.ConvertFromString("#D23D6F"); // Pink
    // Desaturate and lighten from material pink 500
    // https://m2.material.io/design/color/the-color-system.html#tools-for-picking-colors
    // $ pastel color E91E63 | pastel desaturate 0.2 | pastel lighten 0.015

    public Color SecondaryColor
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Theme cur = _paletteHelper.GetTheme();
                cur.SetSecondaryColor(value);
                _paletteHelper.SetTheme(cur);
            }
        }
    } = (Color)ColorConverter.ConvertFromString("#00BCD4"); // Cyan
}

public interface IMenuAction : INotifyPropertyChanged, ICloneable
{
    [JsonIgnore]
    string Type { get; }
    string Title { get; set; }
    bool IsEnabled { get; set; }
}

public class SearchMenuAction : Bindable, IMenuAction
{
    [JsonIgnore]
    public string Type => "Search";
    public required string Title { get; set; }
    public required string Url { get; set => Set(ref field, value); }
    public bool IsEnabled { get; set => Set(ref field, value); } = true;

    public object Clone()
    {
        return new SearchMenuAction
        {
            Title = Title,
            Url = Url,
            IsEnabled = IsEnabled
        };
    }
}

public class ClipboardMenuAction : Bindable, IMenuAction
{
    [JsonIgnore]
    public string Type => "Clipboard";
    public string Title { get; set; } = "Copy";
    public bool ToLower { get; set => Set(ref field, value); }
    public bool IsEnabled { get; set => Set(ref field, value); } = true;

    public object Clone()
    {
        return new ClipboardMenuAction
        {
            Title = Title,
            ToLower = ToLower,
            IsEnabled = IsEnabled
        };
    }
}

public class ClipboardAllMenuAction : Bindable, IMenuAction
{
    [JsonIgnore]
    public string Type => "ClipboardAll";
    public string Title { get; set; } = "Copy All";
    public bool ToLower { get; set => Set(ref field, value); }
    public bool IsEnabled { get; set => Set(ref field, value); } = true;

    public object Clone()
    {
        return new ClipboardAllMenuAction
        {
            Title = Title,
            ToLower = ToLower,
            IsEnabled = IsEnabled
        };
    }
}

public enum WordClickAction
{
    Translation,
    Clipboard,
    ClipboardAll,
    PDIC
}
