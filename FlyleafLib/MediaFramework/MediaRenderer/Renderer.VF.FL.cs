using FlyleafLib.Controls.WPF;
using System.Linq;
using System.Text.Json.Serialization;

using Vortice.Direct3D11;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    void FLFiltersSetup()
    {
        if (!ucfg.flFiltersFilled)
        {
            ucfg.flFiltersFilled = true;

            foreach(var filterSpec in FLFilter.FLFilterSpecs)
            {
                if (ucfg.FLFilters.TryGetValue(filterSpec.Filter, out var userFilter))
                {
                    userFilter.Initialize(this);
                    if (userFilter.Value != userFilter.Default)
                        { ucfg.hasFLFilters = true; FLSetFilter(userFilter); }
                }
                else
                {
                    lock (ucfg.lockFLFilters)
                        ucfg.FLFilters.Add(filterSpec.Filter, new(this, filterSpec));
                }
            }
        }
        else
        {   // NOTE: psData will not be reset on each setup so make sure we set all
            ucfg.hasFLFilters = false;
            foreach(var userFilter in ucfg.FLFilters.Values)
                if (userFilter.Value != userFilter.Default)
                    { ucfg.hasFLFilters = true; FLSetFilter(userFilter); }
        }
    }
    void FLFiltersSync()
    {
        if (!ucfg.SyncVPFilters || (!ucfg.hasD3Filters && !ucfg.hasFLFilters))
            return;

        ucfg.hasFLFilters = false;

        foreach(var flFilter in ucfg.FLFilters.Values)
            if (ucfg.D3Filters.TryGetValue(ToVideoProcessorFilter(flFilter.Filter), out var d3Filter))
            {
                flFilter.Value = (int)Math.Round(Scale(d3Filter.Value, d3Filter.Minimum, d3Filter.Maximum, flFilter.Minimum, flFilter.Maximum));
                if (flFilter.Value != flFilter.Default)
                    ucfg.hasFLFilters = true;

                FLSetFilter(flFilter);
            }
            else if (flFilter.Value != flFilter.Default)
                ucfg.hasFLFilters = true;
    }
    internal void FLSetFilter(FLFilter cfgFilter, bool request = false)
    {   // Direct Call From Config
        float value = Scale(cfgFilter.Value, cfgFilter.Minimum, cfgFilter.Maximum, cfgFilter.MinimumPS, cfgFilter.MaximumPS);

        switch (cfgFilter.Filter)
        {
            case FLFilters.Brightness:
                psData.Brightness   = value;
                break;

            case FLFilters.Contrast:
                psData.Contrast     = value;
                break;

            case FLFilters.Hue:
                psData.Hue          = value;
                break;

            case FLFilters.Saturation:
                psData.Saturation   = value;
                break;
        }

        var vpRequests = VPRequestType.UpdatePS;

        if (request)
        {
            if (!ucfg.hasFLFilters)
            {
                if (cfgFilter.Value != cfgFilter.Default)
                {
                    ucfg.hasFLFilters = true;
                    if (VideoProcessor != VideoProcessors.D3D11)
                        vpRequests |= VPRequestType.ReConfigVP;
                }
            }
            else if (!ucfg.FLFilters.Where(f => f.Value.Value != f.Value.Default).Any())
            {
                ucfg.hasFLFilters = false;
                if (VideoProcessor != VideoProcessors.D3D11)
                    vpRequests |= VPRequestType.ReConfigVP;
            }

            VPRequest(vpRequests);
        }
        else
            vpRequestsIn |= vpRequests; // during initialization (TBR which is actually the current VP?)
    }

    static VideoProcessorFilter ToVideoProcessorFilter(FLFilters filter) => filter switch
    {
        FLFilters.Brightness    => VideoProcessorFilter.Brightness,
        FLFilters.Contrast      => VideoProcessorFilter.Contrast,
        FLFilters.Hue           => VideoProcessorFilter.Hue,
        FLFilters.Saturation    => VideoProcessorFilter.Saturation,
        _ => default,
    };
}

public class FLFilter : NotifyPropertyChanged
{   // TBR: Publics that currently required for Serialization

    [JsonIgnore]
    public bool         Available   => true; // TBR: FL always available
    public FLFilters    Filter      { get; set; }
    public int          Minimum     { get; set; }
    public float        MinimumPS   { get; set; }
    public int          Maximum     { get; set; }
    public float        MaximumPS   { get; set; }
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
                renderer?.FLSetFilter(this, true);
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

    Renderer renderer;

    public FLFilter() { }

    internal FLFilter(Renderer renderer, FLFilterSpec filterSpec)
    {
        Filter      = filterSpec.Filter;
        Minimum     = filterSpec.Minimum;
        MinimumPS   = filterSpec.MinimumPS;
        Maximum     = filterSpec.Maximum;
        MaximumPS   = filterSpec.MaximumPS;
        Step        = filterSpec.Step;
        Default     = Value = filterSpec.Default;
        this.renderer = renderer;
    }

    internal void Initialize(Renderer renderer)
        => this.renderer = renderer;

    internal void Dispose()
        => renderer = null;

    internal static List<FLFilterSpec> FLFilterSpecs =
        [new()
        {
            Filter  = FLFilters.Brightness,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = -0.5f,
            MaximumPS =  0.5f
        },
        new()
        {
            Filter  = FLFilters.Contrast,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = 0f,
            MaximumPS = 2f
        },
        new()
        {
            Filter  = FLFilters.Hue,
            Minimum = -180,
            Maximum = 180,
            Default = 0,
            Step    = 1,

            MinimumPS = -3.14f,
            MaximumPS =  3.14f
        },
        new()
        {
            Filter  = FLFilters.Saturation,
            Minimum = -100,
            Maximum = 100,
            Default = 0,
            Step    = 1,

            MinimumPS = 0,
            MaximumPS = 2
        }];

    internal class FLFilterSpec
    {
        public FLFilters Filter;
        public int      Default;
        public float    Step;
        public int      Minimum;
        public float    MinimumPS;
        public int      Maximum;
        public float    MaximumPS;
    }
}
