using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using FlyleafLib;
using LLPlayer.Extensions;
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

    public static JsonSerializerOptions GetJsonSerializerOptions()
    {
        JsonSerializerOptions jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(),
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
    }

    public void FlyleafHostLoaded()
    {
        FL.Player.renderer.ViewportChanged += (sender, args) =>
        {
            Utils.UIIfRequired(UpdateSubsConfig);
        };
    }

    public void SaveAfter()
    {
        // Update initial value
        _subsPositionInitial = SubsPosition;
    }

    public string SubsFontFamily { get; set => Set(ref field, value); } = "Segoe UI";

    // Primary Subtitle Size
    public double SubsFontSize { get; set => Set(ref field, value); } = 44;

    // Secondary Subtitle Size
    [JsonIgnore]
    public double SubsFontSize2 { get; set => Set(ref field, value); }

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
    } = SubPositionAlignment.Center;

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
    public double SubsDistance { get; set => Set(ref field, value); }

    public double SubsDistanceInitial
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
    } = 11;

    public Color SubsStrokeColor { get; set => Set(ref field, value); } = Colors.Black;

    public double SubsStrokeThickness { get; set => Set(ref field, value); } = 2.8;

    internal void UpdateSubsConfig()
    {
        if (!Loaded)
            return;

        // Avoid unnecessary updates
        if (FL.Player.Subtitles[0].Enabled || FL.Player.Subtitles[1].Enabled)
        {
            UpdateSubsDistance();
            UpdateSubsMargin();
        }
    }

    private void UpdateSubsDistance()
    {
        if (!Loaded)
            return;

        if (FL.Player.Playlist.Selected != null)
        {
            int videoHeight = FL.Player.VideoDecoder.Height;
            float viewportHeight = FL.Player.renderer.GetViewport.Height;

            float ratio = viewportHeight / videoHeight;
            double newDistance = SubsDistanceInitial * ratio;

            SubsDistance = newDistance;
        }
    }

    private void UpdateSubsMargin()
    {
        if (!Loaded)
            return;

        // Set the margin from the top based on Viewport, not Window
        // Allow going above or below the Viewport
                var viewport = FL.Player.renderer.GetViewport;

        float offset = viewport.Y;
        float height = viewport.Height;

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

        double bottomMargin = (FL!.Player.renderer.GetViewport.Height * (SubsFixOverflowMargin / 100.0));

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
}

public enum SubPositionAlignment
{
    Top,    // This is useful for dual subs because primary sub position is stayed
    Center, // Same as bitmap subs
    Bottom  // Normal video players use this
}
