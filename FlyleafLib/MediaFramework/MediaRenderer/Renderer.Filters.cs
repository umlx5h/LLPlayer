using System.Linq;
using System.Text.Json.Serialization;

using Vortice.Direct3D11;

using FlyleafLib.Controls.WPF;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    bool alreadySetup;
    void SetupFilters()
    {
        var cfgFLFilters = Config.Video.Filters;
        var cfgD3Filters = Config.Video.D3Filters;

        if (alreadySetup) // NOTE: Possible different values for D3D11 Filters?*
        {
            if (HasFLFilters)
                foreach(var cfgFLFilter in cfgFLFilters.Values)
                    UpdateFLFilterValue(cfgFLFilter, false);

            foreach(var cfgD3Filter in cfgD3Filters.Values)
                UpdateD3FilterValue(cfgD3Filter, false);

            return;
        }

        alreadySetup = true;

        var d3Filters = curVPCC.Filters;

        foreach(VideoFilters filter in Enum.GetValues(typeof(VideoFilters)))
        {
            if (flFilters.TryGetValue(filter, out var flFilter))
            {
                if (cfgFLFilters.TryGetValue(filter, out var cfgFLFilter))
                {
                    cfgFLFilter.SetFilter(this, flFilter);
                    if (cfgFLFilter.Value != cfgFLFilter.Default)
                        HasFLFilters = true;
                    if (cfgFLFilter.Value != cfgFLFilter.Default)
                        UpdateFLFilterValue(cfgFLFilter, false);
                }
                else
                {
                    cfgFLFilter = new(this, flFilter);
                    cfgFLFilters.Add(filter, cfgFLFilter);
                }
            }

            if (d3Filters.TryGetValue(filter, out var d3Filter))
            {
                if (cfgD3Filters.TryGetValue(filter, out var cfgD3Filter))
                {
                    cfgD3Filter.SetFilter(this, d3Filter);
                    if (cfgD3Filter.Value != cfgD3Filter.Default)
                        UpdateD3FilterValue(cfgD3Filter, false);
                }
                else
                {
                    cfgD3Filter = new(this, d3Filter);
                    cfgD3Filters.Add(filter, cfgD3Filter);
                }
            }
        }
    }

    internal void UpdateFLFilterValue(FLVideoFilter cfgFilter, bool present = true)
    {
        lock (lockDevice)
        {
            if (Disposed)
                return;

            float value = Scale(cfgFilter.Value, cfgFilter.Minimum, cfgFilter.Maximum, cfgFilter.filter.MinimumPS, cfgFilter.filter.MaximumPS);

            switch (cfgFilter.Filter)
            {
                case VideoFilters.Brightness:

                    psBufferData.brightness = value;
                    break;

                case VideoFilters.Contrast:
                    psBufferData.contrast = value;
                    break;

                case VideoFilters.Hue:
                    psBufferData.hue = value;
                    break;

                case VideoFilters.Saturation:
                    psBufferData.saturation = value;
                    break;
            }

            context.UpdateSubresource(psBufferData, psBuffer);

            // Switch pixel shader (w/o filters) only if required
            var hasFilters = cfgFilter.Value != cfgFilter.Default;
            if (hasFilters != HasFLFilters)
            {
                if (hasFilters)
                {
                    HasFLFilters = true;
                    if (videoProcessor == VideoProcessors.Flyleaf)
                        ConfigPlanes();
                }
                else if (!Config.Video.Filters.Where(f => f.Value.Value != f.Value.Default).Any())
                {
                    HasFLFilters = false;
                    if (videoProcessor == VideoProcessors.Flyleaf)
                        ConfigPlanes();
                }
            }

            if (present)
                RenderRequest();
        }
    }
    internal void UpdateD3FilterValue(D3VideoFilter cfgFilter, bool present = true)
    {
        lock (lockDevice)
        {
            if (Disposed || vc == null)
                return;

            vc.VideoProcessorSetStreamFilter(vp, 0, ConvertFromVideoProcessorFilterCaps((VideoProcessorFilterCaps)cfgFilter.Filter), true,
                (int)Scale(cfgFilter.Value, cfgFilter.Minimum, cfgFilter.Maximum, cfgFilter.filter.Minimum, cfgFilter.filter.Maximum));

            if (present)
                RenderRequest();
        }
    }

    static Dictionary<VideoFilters, VideoFilterLocal> flFilters = new()
    {
        [VideoFilters.Brightness]   = new()
        {
            Filter  = VideoFilters.Brightness,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = -0.5f,
            MaximumPS =  0.5f
        },
        [VideoFilters.Contrast]     = new()
        {
            Filter  = VideoFilters.Contrast,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = 0,
            MaximumPS = 2
        },
        [VideoFilters.Hue]          = new()
        {
            Filter  = VideoFilters.Hue,
            Minimum = -180,
            Maximum = 180,
            Default = 0,
            Step    = 1,

            MinimumPS = -3.14f,
            MaximumPS =  3.14f
        },
        [VideoFilters.Saturation]   = new()
        {
            Filter  = VideoFilters.Saturation,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = 0,
            MaximumPS = 2
        },
    };
}

class VideoFilterLocal
{
    public VideoFilters Filter      { get; set; }
    public int          Minimum     { get; set; }
    public int          Maximum     { get; set; }
    public int          Default     { get; set; }
    public float        Step        { get; set; }

    // Actual for pixel shader
    public float        MinimumPS    { get; set; }
    public float        MaximumPS    { get; set; }
}

public class FLVideoFilter : VideoFilter
{
    public FLVideoFilter() : base() { }
    internal FLVideoFilter(Renderer renderer,VideoFilterLocal filter) : base(renderer, filter) { }

    public override void UpdateValue()
    {
        renderer.UpdateFLFilterValue(this);
        if (renderer.Config.Video.SyncVPFilters && renderer.Config.Video.D3Filters.TryGetValue(Filter, out var d3Filter))
            d3Filter.SetValue2(_Value == _Default ? d3Filter._Default : (int)Scale(_Value, Minimum, Maximum, d3Filter.Minimum, d3Filter.Maximum));
    }

    internal void SetValue2(int value)
    {
        if (_Value == value)
            return;

        _Value = value;
        RaiseUI(nameof(Value));
        renderer.UpdateFLFilterValue(this);
    }
}

public class D3VideoFilter : VideoFilter
{
    public D3VideoFilter() : base() { }
    internal D3VideoFilter(Renderer renderer, VideoFilterLocal filter) : base(renderer, filter) { }

    public override void UpdateValue()
    {
        renderer.UpdateD3FilterValue(this);
        if (renderer.Config.Video.SyncVPFilters && renderer.Config.Video.Filters.TryGetValue(Filter, out var flFilter))
            flFilter.SetValue2(_Value == _Default ? flFilter._Default : (int)Scale(_Value, Minimum, Maximum, flFilter.Minimum, flFilter.Maximum));
    }
    internal void SetValue2(int value)
    {
        if (_Value == value)
            return;

        _Value = value;
        RaiseUI(nameof(Value));
        renderer.UpdateD3FilterValue(this);
    }
}

public abstract class VideoFilter : NotifyPropertyChanged
{
    // NOTE: Don't touch public (constructor/properties) required for serialization

    [JsonIgnore]
    public bool         Available   => filter != null;
    internal VideoFilterLocal filter;
    internal void SetFilter(Renderer renderer, VideoFilterLocal filter)
    {
        this.renderer   = renderer;
        this.filter     = filter;
        RaiseUI(nameof(Available));
    }

    public VideoFilters Filter      { get => _Filter;       set => SetUI(ref _Filter, value); }
    protected VideoFilters _Filter = VideoFilters.Brightness;

    public int          Minimum     { get => _Minimum;      set => SetUI(ref _Minimum, value); }
    protected int _Minimum = 0;

    public int          Maximum     { get => _Maximum;      set => SetUI(ref _Maximum, value); }
    protected int _Maximum = 100;

    public float        Step        { get => _Step;         set => SetUI(ref _Step, value); }
    protected float _Step = 1;

    public int          Default
    {
        get => _Default;
        set
        {
            if (SetUI(ref _Default, value))
            {
                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    }
    internal int _Default = 50;

    public int          Value
    {
        get => _Value;
        set
        {
            int v = value;
            v = Math.Min(v, Maximum);
            v = Math.Max(v, Minimum);

            if (Set(ref _Value, v))
            {
                if (renderer != null)
                    UpdateValue();

                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    }
    protected int _Value = 50;

    public abstract void UpdateValue();

    [JsonIgnore]
    public RelayCommand SetDefaultValue => field ??= new(_ =>
    {
        Value = Default;
    }, _ => Value != Default);

    internal Renderer renderer;

    public VideoFilter() { }
    internal VideoFilter(Renderer renderer, VideoFilterLocal filter)
    {
        this.renderer   = renderer;
        this.filter     = filter;
        _Filter         = filter.Filter;

        _Minimum        = filter.Minimum;
        _Maximum        = filter.Maximum;
        _Default        = filter.Default;
        _Step           = filter.Step;
        _Value          = filter.Default;
    }
}
