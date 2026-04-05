using FlyleafLib.Controls.WPF;
using System.Linq;
using System.Text.Json.Serialization;

using Vortice.Direct3D11;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    void D3FiltersSetup(D3CacheEntry d3CacheEntry, bool needsFillUnlock)
    {
        if (needsFillUnlock)
        {   // Fills Supported FilterSpecs to Cache (1st time only)
            var filterCaps = ve.VideoProcessorCaps.FilterCaps;
            foreach (var filterCap in D3CacheEntry.AllFilterCaps)
                if (filterCaps.HasFlag(filterCap))
                {
                    var filter = ToVideoProcessorFilter(filterCap);
                    var range  = ve.GetVideoProcessorFilterRange(filter);
                    d3CacheEntry.Filters.Add(filter, range);
                }

            d3CacheEntry.Failed = false;
            Monitor.Exit(d3CacheEntry);
        }

        if (!ucfg.d3FiltersFilled)
        {
            ucfg.d3FiltersFilled = true;

            foreach (var filter in D3CacheEntry.AllFilters)
                if (d3CacheEntry.Filters.TryGetValue(filter, out var filterSpec))
                {
                    if (ucfg.D3Filters.TryGetValue(filter, out var userFilter))
                    {   // Initializes Existing Supported Filters in Config (1st time - probably none)
                        userFilter.Initialize(this);
                        if (userFilter.Value != userFilter.Default)
                            { ucfg.hasD3Filters = true; vc.VideoProcessorSetStreamFilter(vp, 0, filter, true, userFilter.Value); }
                    }
                    else
                    {   // Fills Supported FilterSpecs from Cached to Config (1st time only)
                         lock (ucfg.lockD3Filters)
                            ucfg.D3Filters.Add(filter, new(this, filter, filterSpec));
                    }
                }
                else if (ucfg.D3Filters.TryGetValue(filter, out var userFilter))
                    userFilter.Dispose(); // Not Supported
        }
        else
        {   // Initializes Existing Supported Filters in Config and Sets non-default Values
            foreach(var userFilter in ucfg.D3Filters.Values)
            {
                ucfg.hasD3Filters = false;
                userFilter.Initialize(this);
                if (userFilter.Value != userFilter.Default)
                    { ucfg.hasD3Filters = true; vc.VideoProcessorSetStreamFilter(vp, 0, userFilter.Filter, true, userFilter.Value); }
            }
        }
    }
    void D3FiltersSync()
    {
        if (!ucfg.SyncVPFilters || (!ucfg.hasD3Filters && !ucfg.hasFLFilters))
            return;

        ucfg.hasD3Filters = false;

        foreach(var flFilter in ucfg.FLFilters.Values)
            if (ucfg.D3Filters.TryGetValue(ToVideoProcessorFilter(flFilter.Filter), out var d3Filter))
            {
                d3Filter.Value = (int)Math.Round(Scale(flFilter.Value, flFilter.Minimum, flFilter.Maximum, d3Filter.Minimum, d3Filter.Maximum));
                if (d3Filter.Value != d3Filter.Default)
                    ucfg.hasD3Filters = true;

                vc.VideoProcessorSetStreamFilter(vp, 0, d3Filter.Filter, true, d3Filter.Value);
            }
            else if (d3Filter.Value != d3Filter.Default)
                ucfg.hasD3Filters = true;
    }
    internal void D3SetFilter(D3Filter filter)
    {   // Direct Call from Config
        if (filter.Value != filter.Default)
            ucfg.hasD3Filters = true;
        else
            ucfg.hasD3Filters = ucfg.D3Filters.Where(f => f.Value.Value != f.Value.Default).Any();

        if (vc != null)
        {
            vc.VideoProcessorSetStreamFilter(vp, 0, filter.Filter, true, filter.Value);
            RenderRequest();
        }
    }

    static VideoProcessorFilter ToVideoProcessorFilter(VideoProcessorFilterCaps filter) => filter switch
    {
        VideoProcessorFilterCaps.Brightness         => VideoProcessorFilter.Brightness,
        VideoProcessorFilterCaps.Contrast           => VideoProcessorFilter.Contrast,
        VideoProcessorFilterCaps.Hue                => VideoProcessorFilter.Hue,
        VideoProcessorFilterCaps.Saturation         => VideoProcessorFilter.Saturation,
        VideoProcessorFilterCaps.EdgeEnhancement    => VideoProcessorFilter.EdgeEnhancement,
        VideoProcessorFilterCaps.NoiseReduction     => VideoProcessorFilter.NoiseReduction,
        VideoProcessorFilterCaps.AnamorphicScaling  => VideoProcessorFilter.AnamorphicScaling,
        VideoProcessorFilterCaps.StereoAdjustment   => VideoProcessorFilter.StereoAdjustment,
        _ => default,
    };
}

public class D3Filter : NotifyPropertyChanged
{   // NOTE: Serialization requires Public sets and constructor

    [JsonIgnore]
    public bool         Available   => renderer != null;
    public VideoProcessorFilter
                        Filter      { get; set; }
    public int          Minimum     { get; set; }
    public int          Maximum     { get; set; }
    public float        Step        { get; set; }
    public int          Default
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    }
    public int Value
    {
        get => _Value;
        set
        {
            int v = value;
            v = Math.Min(v, Maximum);
            v = Math.Max(v, Minimum);

            if (Set(ref _Value, v))
            {
                renderer?.D3SetFilter(this);
                SetDefaultValue.OnCanExecuteChanged();
            }
        }
    }
    protected int _Value;

    [JsonIgnore]
    public RelayCommand SetDefaultValue => field ??= new(_ =>
    {
        Value = Default;
    }, _ => Value != Default);

    internal Renderer renderer;

    public D3Filter() { }

    internal D3Filter(Renderer renderer, VideoProcessorFilter filter, VideoProcessorFilterRange filterSpec)
    {
        Filter      = filter;
        Minimum     = filterSpec.Minimum;
        Maximum     = filterSpec.Maximum;
        Step        = filterSpec.Multiplier;
        Default     = Value = filterSpec.Default;
        this.renderer = renderer;
    }

    internal void Initialize(Renderer renderer)
    {
        this.renderer = renderer;
        if (!Available) RaiseUI(nameof(Available));
    }

    internal void Dispose()
    {
        renderer = null;
        //RaiseUI(nameof(Available));
    }
}

class D3CacheEntry
{   // Checks Success and Filters support for D3VP once per LUID

    public static VideoProcessorFilter[]        AllFilters      = Enum.GetValues<VideoProcessorFilter>();
    public static VideoProcessorFilterCaps[]    AllFilterCaps   = Enum.GetValues<VideoProcessorFilterCaps>();

    public static Dictionary<long, D3CacheEntry>
                D3Cache = [];

    public Dictionary<VideoProcessorFilter, VideoProcessorFilterRange>
                Filters = [];

    public bool Failed  = true;

    public static D3CacheEntry Get(long luid, out bool needsFillUnlock)
    {
        lock (D3Cache)
        {
            if (D3Cache.TryGetValue(luid, out var d3vp))
            {
                needsFillUnlock = false;
                lock(d3vp) return d3vp;
            }

            needsFillUnlock = true;
            var d3filter = new D3CacheEntry();
            Monitor.Enter(d3filter);
            D3Cache.Add(luid, d3filter);
            return d3filter;
        }
    }
}
